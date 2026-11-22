using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace AyceOc2Bridge;

public sealed partial class Plugin
{
    private static readonly Dictionary<int, bool> KitchenProgressObjects = new();
    private static MethodInfo kitchenCookingUi, kitchenProgressUi, kitchenWaterGun, kitchenStopParticles;
    private static PropertyInfo kitchenProgressTarget, kitchenProgressMover, kitchenProgressCanvas;
    private static PropertyInfo kitchenCanvasRect, kitchenCanvasHeight, kitchenProgressOffset;
    private static FieldInfo kitchenVectorX, kitchenVectorY, kitchenVectorZ;
    private static object clearSprayParticles;
    private static bool kitchenProgressWarning;

    private static void InstallKitchenPresentation(Harmony harmony, Assembly game)
    {
        var hover = game.GetType("HoverIconUIController", true);
        kitchenCookingUi = GenericGameObjectMethod("GetComponent", game.GetType("CookingUIController", true));
        kitchenProgressUi = GenericGameObjectMethod("GetComponent", game.GetType("ProgressUIController", true));
        kitchenProgressTarget = RequireProperty(hover, "m_followTransform");
        kitchenProgressMover = RequireProperty(hover, "m_uiMove");
        kitchenProgressCanvas = RequireProperty(hover, "m_canvasRect");
        kitchenCanvasRect = RequireProperty(kitchenProgressCanvas.PropertyType, "rect");
        kitchenCanvasHeight = RequireProperty(kitchenCanvasRect.PropertyType, "height");
        kitchenProgressOffset = RequireProperty(kitchenProgressMover.PropertyType, "Offset");
        kitchenVectorX = vectorType.GetField("x");
        kitchenVectorY = vectorType.GetField("y");
        kitchenVectorZ = vectorType.GetField("z");

        var particles = Assembly.Load("UnityEngine.ParticleSystemModule");
        var stopMode = particles.GetType("UnityEngine.ParticleSystemStopBehavior", true);
        clearSprayParticles = Enum.Parse(stopMode, "StopEmittingAndClear");
        kitchenStopParticles = Find(particles.GetType("UnityEngine.ParticleSystem", true), "Stop", new[] { typeof(bool), stopMode });
        kitchenWaterGun = GenericGameObjectMethod("GetComponent", game.GetType("WaterGunSpray", true));

        Hook(harmony, Find(hover, "UpdateFollow", Type.EmptyTypes), nameof(AfterKitchenProgressFollow), postfix: true);
        Hook(harmony, Find(game.GetType("ClientSprayingUtensil", true), "StopSpray", Type.EmptyTypes), nameof(BeforeKitchenWaterGunStops));
    }

    private static void ClearKitchenPresentation()
    {
        KitchenProgressObjects.Clear();
        kitchenProgressWarning = false;
    }

    private static float KitchenProgressY(float offset, float height) =>
        Math.Clamp(offset, height * .17f, height * .79f);

    private static void AfterKitchenProgressFollow(object __instance)
    {
        if (sceneHandle == null || overlay?.HasKitchen != true || kitchenProgressWarning)
            return;
        try
        {
            if (kitchenProgressTarget.GetValue(__instance) == null || kitchenProgressMover.GetValue(__instance) is not object mover ||
                kitchenProgressCanvas.GetValue(__instance) is not object canvas)
                return;
            var item = Get(__instance, "gameObject");
            var id = KitchenObjectId(item);
            if (!KitchenProgressObjects.TryGetValue(id, out var progress))
            {
                progress = kitchenCookingUi.Invoke(item, null) != null || kitchenProgressUi.Invoke(item, null) != null;
                KitchenProgressObjects.Add(id, progress);
            }
            if (!progress)
                return;
            var height = (float)kitchenCanvasHeight.GetValue(kitchenCanvasRect.GetValue(canvas));
            if (height <= 0)
                return;
            var offset = kitchenProgressOffset.GetValue(mover);
            var y = (float)kitchenVectorY.GetValue(offset);
            var visible = KitchenProgressY(y, height);
            if (Math.Abs(visible - y) > .01f)
                kitchenProgressOffset.SetValue(mover, Vector(new[] { (float)kitchenVectorX.GetValue(offset), visible, (float)kitchenVectorZ.GetValue(offset) }));
        }
        catch (Exception error)
        {
            kitchenProgressWarning = true;
            log.LogWarning($"BRIDGE_KITCHEN_PROGRESS_FAILED {error.GetBaseException().Message}");
        }
    }

    private static void BeforeKitchenWaterGunStops(object __instance)
    {
        if (sceneHandle == null || overlay?.Stock == null)
            return;
        try
        {
            var item = Get(__instance, "gameObject");
            if (Get(__instance, "m_sprayEffect") is object effect && kitchenWaterGun.Invoke(item, null) != null)
                kitchenStopParticles.Invoke(effect, new[] { (object)true, clearSprayParticles });
        }
        catch (Exception error) { log.LogWarning($"BRIDGE_WATER_GUN_EFFECT_FAILED {error.GetBaseException().Message}"); }
    }
}
