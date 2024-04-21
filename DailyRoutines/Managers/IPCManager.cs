using System.Reflection;
using System;
using System.Collections.Generic;
using System.Linq;
using DailyRoutines.IPC;
using DailyRoutines.Modules;

namespace DailyRoutines.Managers;

public class IPCManager
{
    public Dictionary<Type, DailyIPCBase> IPCs = [];
    public Dictionary<Type, HashSet<DailyModuleBase>> IPCRegState = [];

    public void Init()
    {
        var types = Assembly.GetExecutingAssembly().GetTypes()
                            .Where(t => typeof(DailyIPCBase).IsAssignableFrom(t) &&
                                        !t.IsAbstract &&
                                        t.GetConstructor(Type.EmptyTypes) != null);

        foreach (var type in types)
        {
            var instance = Activator.CreateInstance(type);
            if (instance is DailyIPCBase ipc)
            {
                if (string.IsNullOrWhiteSpace(ipc.InternalName)) continue;
                IPCs.Add(type, ipc);
                IPCRegState.Add(type, []);
            }
        }
    }

    public T? Load<T>(DailyModuleBase sourceModule) where T : DailyIPCBase
    {
        var ipcName = typeof(T).Name;
        if (!IPCs.TryGetValue(typeof(T), out var instance))
        {
            Service.Log.Error($"Fail to fetch IPC {ipcName}");
            return null;
        }

        try
        {
            if (!IsPluginEnabled(instance.InternalName))
            {
                Service.Log.Error($"Fail to load IPC due to the issue that {instance.InternalName} is not installed or enabled!");
                return null;
            }
            if (!instance.Initialized)
            {
                instance.Init();
                instance.Initialized = true;
                Service.Log.Debug($"Loaded IPC {ipcName}");
            }

            IPCRegState[typeof(T)].Add(sourceModule);

            return (T)instance;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Fail to load IPC {typeof(T).Name} due to errors: {ex.Message}");
            Service.Log.Error(ex.StackTrace ?? "Unknown");
        }

        return null;
    }

    public bool Unload<T>(DailyModuleBase sourceModule) where T : DailyIPCBase
    {
        var ipcName = typeof(T).Name;
        if (!IPCs.TryGetValue(typeof(T), out var instance))
        {
            Service.Log.Error($"Fail to fetch IPC {ipcName}");
            return false;
        }

        try
        {
            if (!instance.Initialized)
            {
                Service.Log.Error($"Fail to unload IPC {ipcName} due to the error that the IPC hasn't been loaded");
                return false;
            }

            var state = IPCRegState[typeof(T)];
            state.Remove(sourceModule);
            if (state.Count <= 0)
            {
                instance.Uninit();
                instance.Initialized = false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Fail to unload IPC {typeof(T).Name} due to errors: {ex.Message}");
            Service.Log.Error(ex.StackTrace ?? "Unknown");
        }

        return false;
    }

    public static bool IsPluginEnabled(string internalName)
        => Service.PluginInterface.InstalledPlugins.FirstOrDefault(x => x.InternalName == internalName && x.IsLoaded) != null;

    public void Uninit()
    {
        foreach (var ipc in IPCs.Values)
            try
            {
                if (ipc.Initialized)
                {
                    ipc.Uninit();
                    ipc.Initialized = false;
                }
            }
            catch (Exception ex)
            {
                Service.Log.Error(ex, $"Fail to unload {ipc.GetType().Name} IPC");
            }

        IPCs.Clear();
        IPCRegState.Clear();
    }
}
