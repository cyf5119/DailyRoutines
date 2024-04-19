using ClickLib;
using DailyRoutines.Infos;
using DailyRoutines.Infos.Clicks;
using DailyRoutines.Managers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace DailyRoutines.Modules;

[ModuleDescription("AutoCTSTitle", "AutoCTSDescription", ModuleCategories.GoldSaucer)]
public class AutoHummer : DailyModuleBase
{
    public override void Init()
    {
        TaskManager ??= new TaskManager { AbortOnTimeout = true, TimeLimitMS = 10000, ShowDebug = false };
        Service.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Hummer", OnAddonSetup);
    }

    public override void ConfigUI() => ConflictKeyText();

    private void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        if (InterruptByConflictKey()) return;

        TaskManager.Enqueue(WaitSelectStringAddon);
        TaskManager.Enqueue(ClickGameButton);
    }

    private unsafe bool? WaitSelectStringAddon()
    {
        if (InterruptByConflictKey()) return true;

        if (TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
            return Click.TrySendClick("select_string1");

        return false;
    }

    private unsafe bool? ClickGameButton()
    {
        if (InterruptByConflictKey()) return true;

        if (TryGetAddonByName<AtkUnitBase>("Hummer", out var addon) && IsAddonReady(addon))
        {
            var button = addon->GetButtonNodeById(29);
            if (button == null || !button->IsEnabled) return false;

            addon->IsVisible = false;

            ClickHummer.Using((nint)addon).Play(3);

            // 只是纯粹因为游玩动画太长了而已
            TaskManager.DelayNext(5000);
            TaskManager.Enqueue(StartAnotherRound);
            return true;
        }

        return false;
    }

    private unsafe bool? StartAnotherRound()
    {
        if (InterruptByConflictKey()) return true;

        if (IsOccupied()) return false;
        var machineTarget = Service.Target.PreviousTarget;
        var machine = machineTarget.Name.ExtractText().Contains("强袭水晶塔") ? (GameObject*)machineTarget.Address : null;

        if (machine != null)
        {
            TargetSystem.Instance()->InteractWithObject(machine);
            return true;
        }

        return false;
    }

    public override void Uninit()
    {
        Service.AddonLifecycle.UnregisterListener(OnAddonSetup);

        base.Uninit();
    }
}
