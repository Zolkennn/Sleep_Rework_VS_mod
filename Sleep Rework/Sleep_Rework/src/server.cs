using System;
using System.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Custom_Sleep;

public class server : ModSystem
{
    private bool enoughToSleeping;
    private double FailSafeDate;
    private float GameSpeedBoost;
    private ICoreServerAPI sapi;
    private IServerNetworkChannel serverChannel;
    private IServerPlayer[] serverOpPlayerArray = Array.Empty<IServerPlayer>();
    private long? ServerSleepEventId;
    private bool tickShoudStop;

    public override bool ShouldLoad(EnumAppSide side)
    {
        return side == EnumAppSide.Server;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;

        api.Event.RegisterEventBusListener(SomeoneSleep, 0.5d, "sleepServer");
        api.Event.RegisterEventBusListener(SomeoneUnSleep, 0.5d, "unsleepServer");

        api.Event.PlayerNowPlaying += EventPlayerJoin;
        api.Event.PlayerDisconnect += EventPlayerLeave;
        api.Event.SaveGameLoaded += EventSaveGameLoaded;

        serverChannel = api.Network.RegisterChannel("sleeping")
            .RegisterMessageType(typeof(NetworksMessageAllSleepMode));
    }

    private void SomeoneSleep(string eventname, ref EnumHandling handling, IAttribute data)
    {
        //sapi.Logger.Debug("Receive sleep from someone");
        ServerSleepEventId ??= sapi.Event.RegisterGameTickListener(ServerSleepTick, 20);
        FailSafeDate = sapi.World.Calendar.ElapsedHours;


        if (checkValidToSleep())
        {
            var message = new NetworksMessageAllSleepMode();
            message.On = true;
            serverChannel.BroadcastPacket(message);
            enoughToSleeping = true;
        }
    }

    private bool checkValidToSleep(string bypassUID = null)
    {
        if (sapi.World.Calendar.HourOfDay is > 7 and < 22) return false;

        var (playerSleeping, playerNotSleeping, opplayersleeping) = NumberPlayerSleeping(bypassUID);

        if (opplayersleeping + playerSleeping == 0) return false;

        var percentPlayerSleeping = playerSleeping / (float)(playerSleeping + playerNotSleeping);

        if (percentPlayerSleeping < 0.80)
        {
            foreach (var player in serverOpPlayerArray)
                player.SendIngameError("", $"{percentPlayerSleeping * 100}%/80% joueurs dorment");

            return false;
        }

        //foreach (var player in serverOpPlayerArray)
        //{
        //    int hour = (int)sapi.World.Calendar.HourOfDay;
        //    int minute = (int)((sapi.World.Calendar.HourOfDay - hour) * 60f);
        //    player.SendIngameError("", $"Bonne nuit: {hour:00}:{minute:00}");
        //}
        return true;
    }

    private (int playerSleeping, int playerNotSleeping, int opplayersleeping) NumberPlayerSleeping(string bypassUID)
    {
        var playerSleeping = 0;
        var playerNotSleeping = 0;
        var Opplayersleeping = 0;
        var allOnlinePlayers = sapi.World.AllOnlinePlayers as IServerPlayer[];
        foreach (var serverPlayer in allOnlinePlayers)
            if (serverPlayer!.ConnectionState == EnumClientState.Playing &&
                serverPlayer.WorldData.CurrentGameMode != EnumGameMode.Spectator && serverPlayer.PlayerUID != bypassUID)
            {
                var behavior = serverPlayer.Entity.GetBehavior<EntityBehaviorTiredness>();
                var isOp = serverOpPlayerArray.Contains(serverPlayer);
                if ((behavior != null ? behavior.IsSleeping ? 1 : 0 : 0) != 0)
                {
                    if (isOp) ++Opplayersleeping;
                    ++playerSleeping;
                    continue;
                }

                if (!isOp) ++playerNotSleeping;
            }

        return (playerSleeping, playerNotSleeping, Opplayersleeping);
    }

    private void SomeoneUnSleep(string eventname, ref EnumHandling handling, IAttribute data)
    {
        //sapi.Logger.Debug("Receive Unsleep from someone");

        if (checkValidToSleep()) return;

        if (ServerSleepEventId != null) tickShoudStop = true;


        if (!enoughToSleeping) return;
        
        var message = new NetworksMessageAllSleepMode();
        message.On = false;
        serverChannel.BroadcastPacket(message);
        enoughToSleeping = false;
    }

    private void ServerSleepTick(float dt)
    {
        //sapi.Logger.Debug($"{sapi.World.Calendar.ElapsedHours} | {FailSafeDate}");
        if (sapi.World.Calendar.ElapsedHours - FailSafeDate > 24)
        {
            sapi.BroadcastMessageToAllGroups("Erreur MASSIVE du système de someils ne plus utilise les lits !!!",
                EnumChatType.AllGroups);

            Debug.Assert(ServerSleepEventId != null, nameof(ServerSleepEventId) + " != null");
            sapi.Event.UnregisterGameTickListener((long)ServerSleepEventId);
            ServerSleepEventId = null;
            sapi.World.Calendar.SetTimeSpeedModifier("sleeping", 0);
            tickShoudStop = false;

            return;
        }

        var hour = (int)sapi.World.Calendar.HourOfDay;
        var minute = (int)((sapi.World.Calendar.HourOfDay - hour) * 60f);
        //sapi.Logger.Debug(
        //    $"{hour:00}:{minute:00} | enoughToSleeping: {enoughToSleeping} | tickShoudStop: {tickShoudStop} | GameSpeedBoost: {GameSpeedBoost}");

        if (enoughToSleeping && sapi.World.Config.GetString("temporalStormSleeping", "0").ToInt() == 0 &&
            sapi.ModLoader.GetModSystem<SystemTemporalStability>().StormStrength > 0.0)
        {
            WakeAllPlayers();
        }
        else
        {
            if (tickShoudStop && GameSpeedBoost == 0.0 && !enoughToSleeping)
            {
                Debug.Assert(ServerSleepEventId != null, nameof(ServerSleepEventId) + " != null");
                sapi.Event.UnregisterGameTickListener((long)ServerSleepEventId);
                ServerSleepEventId = null;
                sapi.World.Calendar.SetTimeSpeedModifier("sleeping", 0);
                tickShoudStop = false;
                return;
            }

            if (GameSpeedBoost <= 0.0 && !enoughToSleeping)
            {
                if (!checkValidToSleep()) return;

                var message = new NetworksMessageAllSleepMode();
                message.On = true;
                serverChannel.BroadcastPacket(message);
                enoughToSleeping = true;
                return;
            }

            GameSpeedBoost = GameMath.Clamp(GameSpeedBoost + dt * (enoughToSleeping ? 400f : -2000f), 0.0f, 17000f);
            sapi.World.Calendar.SetTimeSpeedModifier("sleeping", (int)GameSpeedBoost);
        }


        if (GameSpeedBoost > 0 && sapi.World.Calendar.HourOfDay is > 7 and < 22) WakeAllPlayers();
    }

    public void WakeAllPlayers()
    {
        sapi.World.Calendar.SetTimeSpeedModifier("sleeping", (int)GameSpeedBoost);

        foreach (var allOnlinePlayer in sapi.World.AllOnlinePlayers)
        {
            var serverPlayer = allOnlinePlayer as IServerPlayer;
            if (serverPlayer.ConnectionState == EnumClientState.Playing &&
                serverPlayer.WorldData.CurrentGameMode != EnumGameMode.Spectator)
            {
                var behavior = serverPlayer.Entity.GetBehavior<EntityBehaviorTiredness>();
                var mountedOn = allOnlinePlayer.Entity?.MountedOn;
                if ((behavior != null ? behavior.IsSleeping ? 1 : 0 : 0) != 0 && mountedOn != null)
                    allOnlinePlayer.Entity.TryUnmount();
            }

            serverPlayer.Entity.GetBehavior<EntityBehaviorTiredness>().Tiredness = 12.0f;
        }

        enoughToSleeping = false;
    }

    private void EventPlayerJoin(IServerPlayer byplayer)
    {
        if (byplayer.HasPrivilege(Privilege.time)) serverOpPlayerArray = serverOpPlayerArray.Append(byplayer);

        var behavior = byplayer.Entity.GetBehavior<EntityBehaviorTiredness>();
        behavior.Tiredness = 12.0f; // todo patch slow tick from EntityBehaviorTiredness
    }

    private void EventPlayerLeave(IServerPlayer byplayer)
    {
        if (serverOpPlayerArray.Contains(byplayer)) serverOpPlayerArray = serverOpPlayerArray.Remove(byplayer);

        if (NumberPlayerSleeping(byplayer.PlayerUID).playerSleeping +
            NumberPlayerSleeping(byplayer.PlayerUID).opplayersleeping == 0) tickShoudStop = true;

        //todo quand joueur quite alors que ct le seul a dormir et quite et que nrml la nuit peut passé
    }

    private void EventSaveGameLoaded()
    {
        sapi.World.Calendar?.RemoveTimeSpeedModifier("sleeping");
        GameSpeedBoost = 0.0f;
    }
}