using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ClickLib.Clicks;
using DailyRoutines.Helpers;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using ECommons.Automation;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets;

namespace DailyRoutines.Modules;

[ModuleDescription("AutoDiscardTitle", "AutoDiscardDescription", ModuleCategories.General)]
public unsafe class AutoDiscard : DailyModuleBase
{
    private enum DiscardBehaviour
    {
        Discard,
        Sell
    }

    private class DiscardItemsGroup : IEquatable<DiscardItemsGroup>
    {
        public string UniqueName { get; set; } = null!;
        public HashSet<uint> Items { get; set; } = [];
        public DiscardBehaviour Behaviour { get; set; } = DiscardBehaviour.Discard;

        public DiscardItemsGroup(){ }

        public DiscardItemsGroup(string name)
        {
            UniqueName = name;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as DiscardItemsGroup);
        }

        public bool Equals(DiscardItemsGroup? other)
        {
            if (other is null || GetType() != other.GetType())
                return false;

            return UniqueName == other.UniqueName;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(UniqueName);
        }

        public static bool operator ==(DiscardItemsGroup? lhs, DiscardItemsGroup? rhs)
        {
            if (lhs is null)
            {
                return rhs is null;
            }
            return lhs.Equals(rhs);
        }

        public static bool operator !=(DiscardItemsGroup lhs, DiscardItemsGroup rhs)
        {
            return !(lhs == rhs);
        }
    }

    private class Config : ModuleConfiguration
    {
        public readonly List<DiscardItemsGroup> DiscardGroups = [];
        public bool EnableCommand;
    }

    private static readonly Dictionary<DiscardBehaviour, string> DiscardBehaviourLoc = new()
    {
        { DiscardBehaviour.Discard, Service.Lang.GetText("AutoDiscard-Discard") },
        { DiscardBehaviour.Sell, Service.Lang.GetText("AutoDiscard-Sell") }
    };
    private static readonly InventoryType[] InventoryTypes =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3,
        InventoryType.Inventory4
    ];
    private const string ModuleCommand = "/pdrdiscard";

    private static Config ModuleConfig = null!;

    private static string NewGroupNameInput = string.Empty;
    private static string EditGroupNameInput = string.Empty;

    private static string ItemSearchInput = string.Empty;
    private static string SelectedItemSearchInput = string.Empty;
    private static Dictionary<string, Item>? ItemNames;
    private static Dictionary<string, Item> _ItemNames = [];

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        ItemNames ??= LuminaCache.Get<Item>()
                                 .Where(x => !string.IsNullOrEmpty(x.Name.RawString))
                                 .GroupBy(x => x.Name.RawString)
                                 .ToDictionary(x => x.Key, x => x.First());
        _ItemNames = ItemNames.Take(10).ToDictionary(x => x.Key, x => x.Value);

        TaskManager ??= new TaskManager { AbortOnTimeout = true, TimeLimitMS = 10000, ShowDebug = false };

        if (ModuleConfig.EnableCommand)
            Service.CommandManager.AddCommand(ModuleCommand, 
                                              new CommandInfo(OnCommand) 
                                              { HelpMessage = Service.Lang.GetText("AutoDiscard-CommandHelp"), ShowInHelp = true });
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(Service.Lang.GetText("AutoDiscard-AddCommand", ModuleCommand), ref ModuleConfig.EnableCommand))
        {
            if (ModuleConfig.EnableCommand)
                Service.CommandManager.AddCommand(ModuleCommand, 
                                                  new CommandInfo(OnCommand) 
                                                      { HelpMessage = Service.Lang.GetText("AutoDiscard-CommandHelp"), ShowInHelp = true });
            else
                Service.CommandManager.RemoveCommand(ModuleCommand);
            SaveConfig(ModuleConfig);
        }

        ImGuiOm.HelpMarker(Service.Lang.GetText("AutoDiscard-CommandHelp1", ModuleCommand));

        if (ImGui.BeginTable("DiscardGroupTable", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            var orderColumnWidth = ImGui.CalcTextSize((ModuleConfig.DiscardGroups.Count + 1).ToString()).X + 24;
            ImGui.TableSetupColumn("Order", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, orderColumnWidth);
            ImGui.TableSetupColumn("UniqueName", ImGuiTableColumnFlags.None, 20);
            ImGui.TableSetupColumn("Items", ImGuiTableColumnFlags.None, 60);
            ImGui.TableSetupColumn("Behaviour", ImGuiTableColumnFlags.None, 20);
            ImGui.TableSetupColumn("Operations", ImGuiTableColumnFlags.None, 30);

            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);

            ImGui.TableNextColumn();
            OrderHeader();

            ImGui.TableNextColumn();
            ImGuiOm.Text(Service.Lang.GetText("Name"));

            ImGui.TableNextColumn();
            ImGuiOm.Text(Service.Lang.GetText("AutoDiscard-ItemsOverview"));

            ImGui.TableNextColumn();
            ImGuiOm.Text(Service.Lang.GetText("Mode"));

            ImGui.TableNextColumn();
            ImGuiOm.Text(Service.Lang.GetText("Operation"));

            for (var i = 0; i < ModuleConfig.DiscardGroups.Count; i++)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGuiOm.TextCentered($"{i + 1}");

                ImGui.TableNextColumn();
                UniqueNameColumn(i);

                ImGui.TableNextColumn();
                ItemsColumn(i);

                ImGui.TableNextColumn();
                BehaviourColumn(i);

                ImGui.TableNextColumn();
                OperationColumn(i);
            }

            ImGui.EndTable();
        }
    }

    #region Table
    private void OrderHeader()
    {
        if (ImGuiOm.SelectableIconCentered("AddNewGroup", FontAwesomeIcon.Plus))
            ImGui.OpenPopup("AddNewGroupPopup");

        if (ImGui.BeginPopup("AddNewGroupPopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint("###NewGroupNameInput", Service.Lang.GetText("AutoDiscard-AddNewGroupInputNameHelp"), ref NewGroupNameInput, 100);

            if (ImGui.Button(Service.Lang.GetText("Confirm")))
            {
                var info = new DiscardItemsGroup(NewGroupNameInput);
                if (!string.IsNullOrWhiteSpace(NewGroupNameInput) && !ModuleConfig.DiscardGroups.Contains(info))
                {
                    ModuleConfig.DiscardGroups.Add(info);
                    SaveConfig(ModuleConfig);
                    NewGroupNameInput = string.Empty;

                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.SameLine();
            if (ImGui.Button(Service.Lang.GetText("Cancel")))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }
    }

    private void UniqueNameColumn(int index)
    {
        var group = ModuleConfig.DiscardGroups[index];

        ImGui.PushID(index);
        if (ImGuiOm.SelectableFillCell($"{group.UniqueName}"))
        {
            EditGroupNameInput = group.UniqueName;
            ImGui.OpenPopup("EditGroupPopup");
        }

        if (ImGui.BeginPopup("EditGroupPopup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
            ImGui.InputTextWithHint("###EditGroupNameInput", Service.Lang.GetText("AutoDiscard-AddNewGroupInputNameHelp"), ref EditGroupNameInput, 100);

            if (ImGui.Button(Service.Lang.GetText("Confirm")))
            {
                if (!string.IsNullOrWhiteSpace(EditGroupNameInput) && !ModuleConfig.DiscardGroups.Contains(new(EditGroupNameInput)))
                {
                    ModuleConfig.DiscardGroups.FirstOrDefault(x => x.UniqueName == group.UniqueName).UniqueName = EditGroupNameInput;

                    SaveConfig(ModuleConfig);
                    EditGroupNameInput = string.Empty;

                    ImGui.CloseCurrentPopup();
                }
            }

            ImGui.SameLine();
            if (ImGui.Button(Service.Lang.GetText("Cancel")))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }
        ImGui.PopID();
    }

    private void ItemsColumn(int index)
    {
        var group = ModuleConfig.DiscardGroups[index];
        ImGui.PushID(index);

        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2.5f);
        ImGui.BeginGroup();
        if (group.Items.Count > 0)
        {
            ImGui.BeginGroup();
            foreach (var item in group.Items.TakeLast(10))
            {
                ImGui.Image(ImageHelper.GetIcon(LuminaCache.GetRow<Item>(item).Icon).ImGuiHandle, ImGuiHelpers.ScaledVector2(20f));
                ImGui.SameLine();
            }
            ImGui.EndGroup();
        }
        else
            ImGui.Text(Service.Lang.GetText("AutoDiscard-NoItemInGroupHelp"));
        ImGui.EndGroup();

        if (ImGui.IsItemClicked())
            ImGui.OpenPopup("ItemsEdit");

        if (ImGui.BeginPopup("ItemsEdit"))
        {
            var leftChildSize = new Vector2(200 * ImGuiHelpers.GlobalScale, 300 * ImGuiHelpers.GlobalScale);
            if (ImGui.BeginChild("SelectedItemChild", leftChildSize, true))
            {
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint("###SelectedItemSearchInput", Service.Lang.GetText("PleaseSearch"), ref SelectedItemSearchInput, 100);

                ImGui.Separator();
                foreach (var item in group.Items)
                {
                    var specificItem = LuminaCache.GetRow<Item>(item);
                    if (!string.IsNullOrWhiteSpace(SelectedItemSearchInput) && 
                        !specificItem.Name.RawString.Contains(SelectedItemSearchInput, StringComparison.OrdinalIgnoreCase)) continue;

                    if (ImGuiOm.SelectableImageWithText(ImageHelper.GetIcon(specificItem.Icon).ImGuiHandle, ImGuiHelpers.ScaledVector2(24f), specificItem.Name.RawString, false, ImGuiSelectableFlags.DontClosePopups))
                        group.Items.Remove(specificItem.RowId);
                }

                ImGui.EndChild();
            }

            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.SetCursorPosY(ImGui.GetContentRegionAvail().Y / 2 -  24f);
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0));
            ImGui.BeginDisabled();
            ImGuiOm.ButtonIcon("DecoExchangeIcon", FontAwesomeIcon.ExchangeAlt);
            ImGui.EndDisabled();
            ImGui.PopStyleColor();
            ImGui.EndGroup();

            ImGui.SameLine();
            if (ImGui.BeginChild("SearchItemChild", leftChildSize, true))
            {
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputTextWithHint("###GameItemSearchInput", Service.Lang.GetText("PleaseSearch"), ref ItemSearchInput, 100);

                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    if (!string.IsNullOrWhiteSpace(ItemSearchInput) && ItemSearchInput.Length > 1)
                    {
                        _ItemNames = ItemNames.Where(x => x.Key.Contains(ItemSearchInput, StringComparison.OrdinalIgnoreCase))
                                              .ToDictionary(x => x.Key, x => x.Value);
                    }
                }

                ImGui.Separator();
                foreach (var (itemName, item) in _ItemNames)
                {
                    if (ImGuiOm.SelectableImageWithText(ImageHelper.GetIcon(item.Icon).ImGuiHandle, ImGuiHelpers.ScaledVector2(24f), itemName,
                                                        group.Items.Contains(item.RowId), ImGuiSelectableFlags.DontClosePopups))
                    {
                        if (!group.Items.Remove(item.RowId))
                            group.Items.Add(item.RowId);
                        SaveConfig(ModuleConfig);
                    }
                }

                ImGui.EndChild();
            }

            ImGui.EndPopup();
        }

        ImGui.PopID();
    }

    private void BehaviourColumn(int index)
    {
        var group = ModuleConfig.DiscardGroups[index];
        ImGui.PushID(index);
        foreach (var behaviourPair in DiscardBehaviourLoc)
        {
            if (ImGui.RadioButton(behaviourPair.Value, behaviourPair.Key == group.Behaviour))
            {
                group.Behaviour = behaviourPair.Key;
                SaveConfig(ModuleConfig);
            }

            ImGui.SameLine();
        }
        ImGui.PopID();
    }

    private void OperationColumn(int index)
    {
        var group = ModuleConfig.DiscardGroups[index];

        ImGui.PushID(index);
        ImGui.BeginDisabled(TaskManager.IsBusy);
        if (ImGuiOm.ButtonIcon($"Run_{index}", FontAwesomeIcon.Play, Service.Lang.GetText("Run")))
            EnqueueDiscardGroup(group);
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGuiOm.ButtonIcon($"Stop_{index}", FontAwesomeIcon.Stop, Service.Lang.GetText("Stop")))
            TaskManager.Abort();

        ImGui.BeginDisabled(TaskManager.IsBusy);
        ImGui.SameLine();
        if (ImGuiOm.ButtonIcon($"Copy_{index}", FontAwesomeIcon.Copy, Service.Lang.GetText("Copy")))
        {
            var newGroup = new DiscardItemsGroup(GenerateUniqueName(group.UniqueName))
            {
                Behaviour = group.Behaviour,
                Items = group.Items
            };

            ModuleConfig.DiscardGroups.Add(newGroup);
            SaveConfig(ModuleConfig);
        }

        ImGui.SameLine();
        if (ImGuiOm.ButtonIcon($"Delete_{index}", FontAwesomeIcon.TrashAlt, Service.Lang.GetText("AutoDiscard-DeleteWhenHoldCtrl")))
        {
            if (ImGui.IsKeyDown(ImGuiKey.LeftCtrl))
            {
                ModuleConfig.DiscardGroups.Remove(group);
                SaveConfig(ModuleConfig);
            }
        }
        ImGui.EndDisabled();
        ImGui.PopID();
    }
    #endregion

    private void OnCommand(string command, string arguments)
    {
        EnqueueDiscardGroup(arguments.Trim());
    }

    public void EnqueueDiscardGroup(int index)
    {
        if (index < 0 || index > ModuleConfig.DiscardGroups.Count) return;
        var group = ModuleConfig.DiscardGroups[index];
        if (group.Items.Count > 0)
            EnqueueDiscardGroup(group);
    }

    public void EnqueueDiscardGroup(string uniqueName)
    {
        var group = ModuleConfig.DiscardGroups.FirstOrDefault(x => x.UniqueName == uniqueName && x.Items.Count > 0);
        if (group == null) return;

        EnqueueDiscardGroup(group);
    }

    private void EnqueueDiscardGroup(DiscardItemsGroup group)
    {
        foreach (var item in group.Items)
        {
            if (!TrySearchItemInInventory(item, out var foundItem) || foundItem.Count <= 0) continue;
            foreach (var fItem in foundItem)
            {
                TaskManager.Enqueue(() => OpenInventoryItemContext(fItem, group.Behaviour));
                TaskManager.Enqueue(ConfirmDiscard);
            }
        }
    }

    private static bool TrySearchItemInInventory(uint itemID, out List<InventoryItem> foundItem)
    {
        foundItem = [];
        if (Service.Gui.GetAddonByName("InventoryExpansion") == nint.Zero) return false;
        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null) return false;

        foreach (var type in InventoryTypes)
        {
            var container = inventoryManager->GetInventoryContainer(type);
            if (container == null) return false;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot->ItemID == itemID)
                {
                    foundItem.Add(*slot);
                }
            }
        }

        return foundItem.Count > 0;
    }

    private static bool? ConfirmDiscard()
    {
        if (!TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon)) return false;

        var handler = new ClickSelectYesNo();
        if (addon->GetNodeById(4)->IsVisible) handler.Confirm();
        handler.Yes();

        return true;
    }

    private bool OpenInventoryItemContext(InventoryItem item, DiscardBehaviour behaviour)
    {
        if (!EzThrottler.Throttle("AutoDiscard-DiscardInventoryItem", 100)) return false;

        var agent = AgentInventoryContext.Instance();
        if (agent == null) return false;

        var inventory0 = (AtkUnitBase*)Service.Gui.GetAddonByName("InventoryExpansion");
        if (inventory0 == null) return false;

        if (!TryGetAddonByName<AtkUnitBase>("ContextMenu", out var contextMenu) || !IsAddonAndNodesReady(contextMenu))
        {
            agent->OpenForItemSlot(item.Container, item.Slot, GetActiveInventoryAddonID());
            return false;
        }

        switch (behaviour)
        {
            case DiscardBehaviour.Discard:
                return ClickHelper.ContextMenu("舍弃");
            case DiscardBehaviour.Sell:
                if ((TryGetAddonByName<AtkUnitBase>("RetainerGrid0", out var addonRetainerGrid) && IsAddonAndNodesReady(addonRetainerGrid)) ||
                    (TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addonRetainerSellList) && IsAddonAndNodesReady(addonRetainerSellList)))
                    return ClickHelper.ContextMenu("委托雇员出售物品");
                if (TryGetAddonByName<AtkUnitBase>("Shop", out var addonShop) && IsAddonAndNodesReady(addonShop))
                    return ClickHelper.ContextMenu("出售");

                Service.Chat.Print(new SeStringBuilder().Append(DRPrefix()).AddUiForeground(" 未找到可用的出售页面", 17).Build());
                TaskManager.Abort();
                return true;
        }

        return false;
    }

    private static uint GetActiveInventoryAddonID()
    {
        var inventory0 = (AtkUnitBase*)Service.Gui.GetAddonByName("Inventory");
        if (inventory0 == null) return 0;

        var inventory1 = (AtkUnitBase*)Service.Gui.GetAddonByName("InventoryLarge");
        if (inventory1 == null) return 0;

        var inventory2 = (AtkUnitBase*)Service.Gui.GetAddonByName("InventoryExpansion");
        if (inventory2 == null) return 0;

        if (IsAddonAndNodesReady(inventory0)) return inventory0->ID;
        if (IsAddonAndNodesReady(inventory1)) return inventory1->ID;
        if (IsAddonAndNodesReady(inventory2)) return inventory2->ID;

        return 0;
    }

    public string GenerateUniqueName(string baseName)
    {
        var existingNames = ModuleConfig.DiscardGroups.Select(x => x.UniqueName).ToHashSet();

        if (!existingNames.Contains(baseName))
            return baseName;

        var counter = 0;
        var numberPart = string.Empty;
        foreach (var c in baseName.Reverse())
        {
            if (char.IsDigit(c))
            {
                numberPart = c + numberPart;
            }
            else
            {
                break;
            }
        }

        if (numberPart.Length > 0)
        {
            counter = int.Parse(numberPart) + 1;
            baseName = baseName[..^numberPart.Length];
        }

        while (true)
        {
            var newName = $"{baseName}{counter}";

            if (!existingNames.Contains(newName))
                return newName;

            counter++;
        }
    }

    public override void Uninit()
    {
        Service.CommandManager.RemoveCommand(ModuleCommand);

        base.Uninit();
    }
}