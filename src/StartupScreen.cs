using System;
using System.Reflection;
using UnityEngine;

namespace HarmonyStartupOptimizer;

// Reads startup visibility without creating game singletons or patching game methods.
internal sealed class StartupScreen
{
    private readonly Func<object?> menu, loading;
    private readonly PropertyInfo currentWindow;
    private readonly FieldInfo loadingCamera;

    internal StartupScreen(Assembly game)
    {
        Type menuType = game.GetType("SeapowerUI.ViewModels.MenuSystemViewModel", true)!;
        Type loadingType = game.GetType("SeaPower.LoadScreen", true)!;
        menu = ExistingSingleton(menuType);
        loading = ExistingSingleton(loadingType);
        currentWindow = menuType.GetProperty("CurrentWindow")
            ?? throw new MissingMemberException(menuType.FullName, "CurrentWindow");
        loadingCamera = loadingType.GetField("cameraObject", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException(loadingType.FullName, "cameraObject");
    }

    internal bool MenuVisible()
    {
        object? view = menu();
        if (view == null || !((Behaviour)view).isActiveAndEnabled || currentWindow.GetValue(view) == null) return false;
        object? loader = loading();
        if (loader == null) return true;
        var camera = (Camera?)loadingCamera.GetValue(loader);
        if (camera == null) throw new InvalidOperationException("The loading screen camera is unavailable.");
        // The main menu can initialize behind the loading screen, including at DONE.
        return !camera.isActiveAndEnabled;
    }

    private static Func<object?> ExistingSingleton(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        MethodInfo exists = type.GetMethod("InstanceExists", flags, null, new[] { typeof(bool) }, null)
            ?? throw new MissingMethodException(type.FullName, "InstanceExists(bool)");
        PropertyInfo instance = type.GetProperty("Instance", flags)
            ?? throw new MissingMemberException(type.FullName, "Instance");
        object[] quiet = { false };
        return () => (bool)exists.Invoke(null, quiet)! ? instance.GetValue(null) : null;
    }
}
