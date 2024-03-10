using System.Collections.Generic;
using System.Linq;
using ClickLib;
using DailyRoutines.Clicks;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DailyRoutines.Modules;

[ModuleDescription("AutoMiniCactpotTitle", "AutoMiniCactpotDescription", ModuleCategories.GoldSaucer)]
public unsafe class AutoMiniCactpot : IDailyModule
{
    public bool Initialized { get; set; }
    public bool WithConfigUI => false;

    private static TaskManager? TaskManager;

    // 从左上到右下
    private static readonly Dictionary<uint, uint> BlockToCallbackIndex = new()
    {
        { 30, 0 },
        { 31, 1 },
        { 32, 2 },
        { 33, 3 },
        { 34, 4 },
        { 35, 5 },
        { 36, 6 },
        { 37, 7 },
        { 38, 8 }
    };

    private static readonly Dictionary<uint, int> LineToUnkNumber3D4 = new()
    {
        { 22, 1 }, // 第一列 (从左到右)
        { 23, 2 }, // 第二列
        { 24, 3 }, // 第二列
        { 26, 5 }, // 第一行 (从上到下) 
        { 27, 6 }, // 第二行
        { 28, 7 }, // 第二行
        { 21, 0 }, // 左侧对角线
        { 25, 4 }  // 右侧对角线
    };

    private static int SelectedLineNumber3D4;

    public void Init()
    {
        TaskManager ??= new TaskManager { AbortOnTimeout = true, TimeLimitMS = 5000, ShowDebug = false };
        Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "LotteryDaily", OnAddonSetup);

        Initialized = true;
    }

    public void ConfigUI() { }

    public void OverlayUI() { }

    private static void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        if (TaskManager.IsBusy) return;

        if (IsEzMiniCactpotInstalled())
        {
            TaskManager.Enqueue(ClickHighlightBlocks);
        }
        else
        {
            TaskManager.Enqueue(RandomClick);
        }
    }

    private static bool? RandomClick()
    {
        if (!WaitLotteryDailyAddon()) return false;
        if (TryGetAddonByName<AtkUnitBase>("LotteryDaily", out var addon) && IsAddonReady(addon))
        {
            addon->GetButtonNodeById(67)->AtkComponentBase.SetEnabledState(true);

            if (!addon->GetButtonNodeById(67)->IsEnabled) return false;

            var clickHandler = new ClickLotteryDailyDR();
            clickHandler.Confirm(0);

            TaskManager.DelayNext(100);
            TaskManager.Enqueue(ClickExit);
            return true;
        }

        return false;
    }

    private static bool? ClickHighlightBlocks()
    {
        if (TryGetAddonByName<AtkUnitBase>("LotteryDaily", out var addon) &&
            HelpersOm.IsAddonAndNodesReady(addon))
        {
            var helpText = addon->GetTextNodeById(39)->NodeText.ExtractText();

            if (helpText.Contains("格子"))
            {
                ClickHighlightBlock();
                return false;
            }

            TaskManager.DelayNext(100);
            TaskManager.Enqueue(ClickHighlightLine);
            return true;
        }

        return false;
    }

    private static bool? ClickHighlightBlock()
    {
        if (TryGetAddonByName<AddonLotteryDaily>("LotteryDaily", out var addon) &&
            HelpersOm.IsAddonAndNodesReady(&addon->AtkUnitBase))
        {
            var handler = new ClickLotteryDailyDR();
            for (var i = 0; i < 3; i++)
            {
                var blockRow1 = addon->GameBoard.Row1[i]->AtkComponentButton.AtkComponentBase.OwnerNode;
                if (blockRow1->AtkResNode is { MultiplyBlue: 0, MultiplyGreen: 100, MultiplyRed: 0 })
                {
                    handler.Block(BlockToCallbackIndex[blockRow1->AtkResNode.NodeID]);
                    return true;
                }

                var blockRow2 = addon->GameBoard.Row2[i]->AtkComponentButton.AtkComponentBase.OwnerNode;
                if (blockRow2->AtkResNode is { MultiplyBlue: 0, MultiplyGreen: 100, MultiplyRed: 0 })
                {
                    handler.Block(BlockToCallbackIndex[blockRow2->AtkResNode.NodeID]);
                    return true;
                }

                var blockRow3 = addon->GameBoard.Row3[i]->AtkComponentButton.AtkComponentBase.OwnerNode;
                if (blockRow3->AtkResNode is { MultiplyBlue: 0, MultiplyGreen: 100, MultiplyRed: 0 })
                {
                    handler.Block(BlockToCallbackIndex[blockRow3->AtkResNode.NodeID]);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool? ClickHighlightLine()
    {
        if (TryGetAddonByName<AddonLotteryDaily>("LotteryDaily", out var addon) &&
            HelpersOm.IsAddonAndNodesReady(&addon->AtkUnitBase))
        {
            for (var i = 0; i < 8; i++)
            {
                var line = addon->LaneSelector[i]->AtkComponentBase.OwnerNode;

                if (line->AtkResNode is { MultiplyBlue: 0, MultiplyGreen: 100, MultiplyRed: 0 })
                {
                    SelectedLineNumber3D4 = LineToUnkNumber3D4[line->AtkResNode.NodeID];
                    addon->UnkNumber3D4 = SelectedLineNumber3D4;

                    TaskManager.DelayNext(100);
                    TaskManager.Enqueue(ClickConfirm);
                    return true;
                }
            }
        }

        return false;
    }

    private static bool? ClickConfirm()
    {
        if (TryGetAddonByName<AddonLotteryDaily>("LotteryDaily", out var addon) &&
            HelpersOm.IsAddonAndNodesReady(&addon->AtkUnitBase))
        {
            var handler = new ClickLotteryDailyDR();
            handler.Confirm(SelectedLineNumber3D4);

            TaskManager.DelayNext(100);
            TaskManager.Enqueue(ClickExit);
            return true;
        }

        return false;
    }

    private static bool? ClickExit()
    {
        if (TryGetAddonByName<AddonLotteryDaily>("LotteryDaily", out var addon) &&
            HelpersOm.IsAddonAndNodesReady(&addon->AtkUnitBase))
        {
            var clickHandler = new ClickLotteryDailyDR();
            clickHandler.Exit();
            addon->AtkUnitBase.Close(true);

            TaskManager.DelayNext(100);
            TaskManager.Enqueue(() => Click.TrySendClick("select_yes"));
            return true;
        }

        return false;
    }

    private static bool WaitLotteryDailyAddon()
    {
        if (TryGetAddonByName<AtkUnitBase>("LotteryDaily", out var addon) &&
            HelpersOm.IsAddonAndNodesReady(addon))
        {
            var welcomeImageState = addon->GetImageNodeById(4)->AtkResNode.IsVisible;
            var selectBlockTextState = addon->GetTextNodeById(3)->AtkResNode.IsVisible;
            var selectLineTextState = addon->GetTextNodeById(2)->AtkResNode.IsVisible;

            if (!welcomeImageState && !selectBlockTextState && !selectLineTextState) return true;
        }

        return false;
    }

    internal static bool IsEzMiniCactpotInstalled()
    {
        return P.PluginInterface.InstalledPlugins.Any(plugin => plugin is { Name: "ezMiniCactpot", IsLoaded: true });
    }

    public void Uninit()
    {
        Service.AddonLifecycle.UnregisterListener(OnAddonSetup);
        TaskManager?.Abort();

        Initialized = false;
    }
}
