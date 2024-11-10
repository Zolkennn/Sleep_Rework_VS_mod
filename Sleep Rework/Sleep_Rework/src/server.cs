using System;
using System.Diagnostics;
using System.Linq;
using CommonLib.Config;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Custom_Sleep;

public class Server : ModSystem
{
    private bool enoughToSleep;
    private bool timeAccelerated;
    private double failSafeDate;
    private float gameSpeedBoost;
    private ICoreServerAPI sapi;
    private IServerNetworkChannel serverChannel;
    private IServerPlayer[] serverOpPlayerArray = Array.Empty<IServerPlayer>(); //todo a supprimer
    private long? serverSleepEventId;
    private bool tickShoudStop;
    private Config config;

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
        
        config = sapi.ModLoader.GetModSystem<ConfigManager>().GetConfig<Config>();

        if (config.ValidHours && config.MorningHours > config.EveningHours) throw new ArgumentException("MorningHours should be < from EveningHours");
    }

    private void SomeoneSleep(string eventname, ref EnumHandling handling, IAttribute data)
    {
        serverSleepEventId ??= sapi.Event.RegisterGameTickListener(ServerSleepTick, 20);
        failSafeDate = sapi.World.Calendar.ElapsedHours;


        if (enoughToSleep || !CheckEnoughToSleep()) return;
        
        //var message = new NetworksMessageAllSleepMode();
        //message.On = true;
        //serverChannel.BroadcastPacket(message);
        enoughToSleep = true;
    }

    private bool CheckEnoughToSleep(string bypassUid = null)
    {
        var (playerSleeping, playerNotSleeping) = NumberPlayerSleeping(bypassUid);

        if (playerNotSleeping == 0) return true;
        if (playerSleeping == 0) return false;

        var percentPlayerSleeping = playerSleeping / (float)(playerSleeping + playerNotSleeping);

        if (!(percentPlayerSleeping < config.SleepingPercent)) return true;

        return false;
    }

    // Case witout counting creative nor spectators players
    private (int playerSleeping, int playerNotSleeping) NumberPlayerSleeping(string bypassUid)
    {
        var playerSleeping = 0;
        var playerNotSleeping = 0;
        var allOnlinePlayers = sapi.World.AllOnlinePlayers as IServerPlayer[];
        var count = allOnlinePlayers.Count(serverPlayer => serverPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative && serverPlayer.WorldData.CurrentGameMode != EnumGameMode.Spectator);
        bool onlyOpPlayer = count == 0;
    
        foreach (var serverPlayer in allOnlinePlayers)
        {
            if (!onlyOpPlayer && !config.CountCreativePlayers && serverPlayer.WorldData.CurrentGameMode == EnumGameMode.Creative) {continue;}
            if (!onlyOpPlayer && !config.CountSpectatorPlayers && serverPlayer.WorldData.CurrentGameMode == EnumGameMode.Spectator) {continue;}

            if (serverPlayer!.ConnectionState != EnumClientState.Playing ||
                serverPlayer.PlayerUID == bypassUid) continue;
            var behavior = serverPlayer.Entity.GetBehavior<EntityBehaviorTiredness>();
            if ((behavior != null ? behavior.IsSleeping ? 1 : 0 : 0) != 0)
            {
                ++playerSleeping;
                continue;
            }

            ++playerNotSleeping;
        }
        
        //sapi.Logger.Debug($"{playerSleeping}, {playerNotSleeping}");
        return (playerSleeping, playerNotSleeping);
    }

    private void SomeoneUnSleep(string eventname, ref EnumHandling handling, IAttribute data)
    {
        //sapi.Logger.Debug("Receive Unsleep from someone");

        if (CheckEnoughToSleep()) return;

        if (serverSleepEventId != null && NumberPlayerSleeping("").playerSleeping == 0) tickShoudStop = true;
        
        if (!enoughToSleep) return;
        
        //var message = new NetworksMessageAllSleepMode();
        //message.On = false;
        //serverChannel.BroadcastPacket(message);
        enoughToSleep = false;
    }

    private void ServerSleepTick(float dt)
    {
        //sapi.Logger.Debug($"{sapi.World.Calendar.ElapsedHours} | {FailSafeDate}");
        if (sapi.World.Calendar.ElapsedHours - failSafeDate > 24)
        {
            sapi.BroadcastMessageToAllGroups("Erreur MASSIVE du système de someils ne plus utilise les lits !!!",
                EnumChatType.AllGroups);

            Debug.Assert(serverSleepEventId != null, nameof(serverSleepEventId) + " != null");
            sapi.Event.UnregisterGameTickListener((long)serverSleepEventId);
            serverSleepEventId = null;
            sapi.World.Calendar.SetTimeSpeedModifier("sleeping", 0);
            tickShoudStop = false;
            gameSpeedBoost = 0;

            return;
        }

        //var hour = (int)sapi.World.Calendar.HourOfDay;
        //var minute = (int)((sapi.World.Calendar.HourOfDay - hour) * 60f);
        //sapi.Logger.Debug($"{hour:00}:{minute:00} | enoughToSleeping: {enoughToSleep && CheckHours()} | tickShoudStop: {tickShoudStop} | GameSpeedBoost: {gameSpeedBoost}");
        
        if (sapi.World.Config.GetString("temporalStormSleeping", "0").ToInt() == 0 &&
            sapi.ModLoader.GetModSystem<SystemTemporalStability>().StormStrength > 0.0)
        {
            // il y a un orrage
            WakeAllPlayers();
        }
        
        if (tickShoudStop && gameSpeedBoost == 0.0)
        {
            Debug.Assert(serverSleepEventId != null, nameof(serverSleepEventId) + " != null");
            sapi.Event.UnregisterGameTickListener((long)serverSleepEventId);
            serverSleepEventId = null;
            sapi.World.Calendar.SetTimeSpeedModifier("sleeping", 0);
            tickShoudStop = false;
            return;
        }
        
        if (gameSpeedBoost == 0 && (!enoughToSleep || !CheckHours())) return;

        //Pas d'accélération mais valid
        if (gameSpeedBoost == 0 && enoughToSleep && CheckHours())
        {
            var message = new NetworksMessageAllSleepMode();
            message.On = true;
            serverChannel.BroadcastPacket(message);
        }

        gameSpeedBoost = GameMath.Clamp(gameSpeedBoost + dt * (enoughToSleep && CheckHours() ? 400f : -2000f), 0.0f, 17000f);
        sapi.World.Calendar.SetTimeSpeedModifier("sleeping", (int)gameSpeedBoost);
        
        if (gameSpeedBoost > 0 && !enoughToSleep || !CheckHours())
        {
            // la partie est accélérer mais n'est plus valide 
            var message = new NetworksMessageAllSleepMode();
            message.On = false;
            serverChannel.BroadcastPacket(message);
        }
    }

    private bool CheckHours()
    {
        //todo config 
        return config.ValidHours && !(sapi.World.Calendar.HourOfDay > config.MorningHours &&
                                     sapi.World.Calendar.HourOfDay < config.EveningHours);
    }

    public void WakeAllPlayers()
    {
        //sapi.Logger.Debug("Server wakeup");
        sapi.World.Calendar.SetTimeSpeedModifier("sleeping", (int)gameSpeedBoost);

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

            
            //todo config Nap
            serverPlayer.Entity.GetBehavior<EntityBehaviorTiredness>().Tiredness = 12.0f;
        }

        enoughToSleep = false;
    }

    private void EventPlayerJoin(IServerPlayer byplayer)
    {
        if (byplayer.HasPrivilege(Privilege.time)) serverOpPlayerArray = serverOpPlayerArray.Append(byplayer);

        //todo config Nap
        var behavior = byplayer.Entity.GetBehavior<EntityBehaviorTiredness>();
        behavior.Tiredness = 12.0f;
    }

    private void EventPlayerLeave(IServerPlayer byplayer)
    {
        if (serverOpPlayerArray.Contains(byplayer)) serverOpPlayerArray = serverOpPlayerArray.Remove(byplayer);

        var numberPlayerSleeping = NumberPlayerSleeping(byplayer.PlayerUID);
        if (serverSleepEventId != null && numberPlayerSleeping.playerSleeping == 0) tickShoudStop = true;

        //todo quand joueur quite alors que ct le seul a dormir et quite et que nrml la nuit peut passé
    }

    private void EventSaveGameLoaded()
    {
        sapi.World.Calendar?.RemoveTimeSpeedModifier("sleeping");
        gameSpeedBoost = 0.0f;
    }
}