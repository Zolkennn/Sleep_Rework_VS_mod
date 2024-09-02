using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Custom_Sleep;

[HarmonyPatch]
public class patch : ModSystem
{
    public Harmony Harmony;

    public override void Start(ICoreAPI api)
    {
        if (!Harmony.HasAnyPatches(Mod.Info.ModID))
        {
            Harmony = new Harmony(Mod.Info.ModID);
            Harmony.PatchAll(); // Applies all harmony patches
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ModSleeping), "ShouldLoad")]
    public static bool ShouldLoad()
    {
        return false;
    }

    //[HarmonyPrefix]
    //[HarmonyPatch(typeof(BlockBed), "OnBlockInteractStart")]
    //public static bool OnBlockInteractStartore(BlockBed __instance, ICoreAPI ___api, IWorldAccessor world)
    //{
    //    if (___api.Side == EnumAppSide.Client && world.Calendar.HourOfDay is > 7 and < 22)
    //    {
    //        (___api as ICoreClientAPI)!.TriggerIngameError(__instance, "nottiredenough", Lang.Get("not-tired-enough"));
    //        return false;
    //    }
    //    return true;
    //}

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BlockBed), "OnBlockInteractStart")]
    public static void OnBlockInteractStart(bool __result, ICoreAPI ___api, IWorldAccessor world)
    {
        if (!__result) return;
        if (___api.Side == EnumAppSide.Client)
        {
            ___api.Event.PushEvent("sleepClient");
            return;
        }

        ___api.Event.PushEvent("sleepServer");
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BlockEntityBed), "DidUnmount")]
    public static void DidUnmount(ICoreAPI ___Api)
    {
        if (___Api.Side == EnumAppSide.Client)
        {
            ___Api.Event.PushEvent("unsleepClient");
            return;
        }

        ___Api.Event.PushEvent("unsleepServer");
    }
    
    [HarmonyPrefix]
    [HarmonyPatch(typeof(BlockEntityBed), "DidMount")]
    public static bool DidMount(BlockEntityBed __instance, EntityAgent entityAgent, ref string ___mountedByPlayerUid, ref long ___mountedByEntityId, ref double ___hoursTotal)
    {
        if (__instance.MountedBy != null && __instance.MountedBy != entityAgent)
        {
            entityAgent.TryUnmount();
        }
        else
        {
            if (__instance.MountedBy == entityAgent)
                return false;
            __instance.MountedBy = entityAgent;
            ___mountedByPlayerUid = entityAgent is EntityPlayer entityPlayer ? entityPlayer.PlayerUID : (string) null;
            ___mountedByEntityId = __instance.MountedBy.EntityId;
            if (__instance.Api.Side == EnumAppSide.Server)
            {
                //__instance.RegisterGameTickListener(new Action<float>(___RestPlayer), 200); //todo à config
                ___hoursTotal = __instance.Api.World.Calendar.TotalHours;
            }
            if (!(__instance.MountedBy?.GetBehavior("tiredness") is EntityBehaviorTiredness behavior))
                return false;
            behavior.IsSleeping = true;
        }

        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(EntityBehaviorTiredness), "SlowTick")]
    public static void SlowTick(EntityBehaviorTiredness __instance)
    {
        if (__instance.entity.World.Side == EnumAppSide.Server && !__instance.IsSleeping)
        {
            __instance.Tiredness = 12;
        }
    }

    public override void Dispose()
    {
        Harmony?.UnpatchAll(Mod.Info.ModID);
    }
}