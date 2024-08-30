using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

public class OLDCode : ModSystem
{
    public bool AllSleeping;
    private ICoreAPI api;
    private ICoreClientAPI capi;
    private IClientNetworkChannel clientChannel;
    private IShaderProgram eyeShaderProg;
    private long? fastEventId;

    public string FragmentShaderCode =
        "\r\n#version 330 core\r\n\r\nin vec2 uv;\r\n\r\nout vec4 outColor;\r\n\r\nuniform float level;\r\n\r\nvoid main () {\r\n    vec2 uvOffseted = vec2(uv.x - 0.5, 2 * (1 + 2*level) * (uv.y - 0.5));\r\n\tfloat strength = 1 - smoothstep(1.1 - level, 0, length(uvOffseted));\r\n\toutColor = vec4(0, 0, 0, min(1, (4 * level - 0.8) + level * strength));\r\n}\r\n";

    public float GameSpeedBoost;
    private EyesOverlayRenderer renderer;
    private ICoreServerAPI sapi;
    private IServerNetworkChannel serverChannel;
    private IServerPlayer[] serverOpPlayerArray = Array.Empty<IServerPlayer>();
    private float sleepLevel;

    public string VertexShaderCode =
        "\r\n#version 330 core\r\n#extension GL_ARB_explicit_attrib_location: enable\r\n\r\nlayout(location = 0) in vec3 vertex;\r\n\r\nout vec2 uv;\r\n\r\nvoid main(void)\r\n{\r\n    gl_Position = vec4(vertex.xy, 0, 1);\r\n    uv = (vertex.xy + 1.0) / 2.0;\r\n}\r\n";

    public override bool ShouldLoad(EnumAppSide side)
    {
        return false;
    }

    public override void Start(ICoreAPI api)
    {
        api.Event.RegisterEventBusListener(sleep, 0.5d, "sleep");
        api.Event.RegisterEventBusListener(unsleep, 0.5d, "unsleep");
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        base.StartServerSide(api);
        this.api = api;
        sapi = api;
        api.Event.PlayerNowPlaying += EventPlayerJoin;
        api.Event.PlayerDisconnect += EventPlayerLeave;
        api.Event.SaveGameLoaded += Event_SaveGameLoaded;
        serverChannel = api.Network.RegisterChannel("sleeping")
            .RegisterMessageType(typeof(NetworksMessageAllSleepMode));
    }

    private void sleep(string eventname, ref EnumHandling handling, IAttribute data)
    {
        fastEventId ??= api.Event.RegisterGameTickListener(FastTick, 20);
        if (api.Side == EnumAppSide.Server && AreAllPlayersSleeping(data?.GetValue() as string))
        {
            var message = new NetworksMessageAllSleepMode();
            message.On = true;
            serverChannel.BroadcastPacket(message);
            AllSleeping = true;
        }
    }

    private void unsleep(string eventname, ref EnumHandling handling, IAttribute data)
    {
        api.Logger.Notification($"{api.Side.ToString()}: Event unsleep catch");
        if (fastEventId != null) api.Event.UnregisterGameTickListener((long)fastEventId); //peut possé probeleme
        fastEventId = null;
        if (api.Side == EnumAppSide.Client)
        {
            sleepLevel = 0;
            renderer.Level = 0;
        }

        GameSpeedBoost = 0.0f;
        api.World.Calendar.SetTimeSpeedModifier("sleeping", 0);
        if (api.Side == EnumAppSide.Server)
            foreach (var player in api.World.AllOnlinePlayers)
                player.Entity.GetBehavior<EntityBehaviorTiredness>().Tiredness = 12.0f;

        if (api.Side == EnumAppSide.Server)
        {
            var message = new NetworksMessageAllSleepMode();
            message.On = false;
            serverChannel.BroadcastPacket(message);
            AllSleeping = false;
            foreach (var player in (IServerPlayer[])sapi.World.AllOnlinePlayers) player.SendIngameError("", "");
        }
    }

    private void EventPlayerJoin(IServerPlayer byplayer)
    {
        if (byplayer.HasPrivilege(Privilege.time)) serverOpPlayerArray = serverOpPlayerArray.Append(byplayer);

        var behavior = byplayer.Entity.GetBehavior<EntityBehaviorTiredness>();
        behavior.Tiredness = 12.0f;
    }

    private void EventPlayerLeave(IServerPlayer byplayer)
    {
        if (serverOpPlayerArray.Contains(byplayer)) serverOpPlayerArray = serverOpPlayerArray.Remove(byplayer);

        if (fastEventId != null) api.Event.PushEvent("sleep", new StringAttribute(byplayer.PlayerUID));
    }

    private void Event_SaveGameLoaded()
    {
        api.World.Calendar?.RemoveTimeSpeedModifier("sleeping");
        GameSpeedBoost = 0.0f;
    }

    public void FastTick(float dt)
    {
        if (api.Side == EnumAppSide.Client)
        {
            if (api.World.Calendar.HourOfDay is > 7 and < 22)
            {
                (api as ICoreClientAPI)!.TriggerIngameError(this, "nottiredenough", Lang.Get("not-tired-enough"));
                return;
            }

            renderer.Level = sleepLevel;
            var flag = capi.World?.Player?.Entity?.MountedOn is BlockEntityBed;
            sleepLevel = GameMath.Clamp(sleepLevel + dt * (!flag || !AllSleeping ? -0.35f : 0.1f), 0.0f,
                0.99f);
        }

        if (api.Side == EnumAppSide.Server)
            //AreAllPlayersSleeping();
            if (AreAllPlayersSleeping() && !AllSleeping)
            {
                var message = new NetworksMessageAllSleepMode();
                message.On = true;
                serverChannel.BroadcastPacket(message);
                AllSleeping = true;
            }

        if (AllSleeping && api.World.Config.GetString("temporalStormSleeping", "0").ToInt() == 0 &&
            api.ModLoader.GetModSystem<SystemTemporalStability>().StormStrength > 0.0)
        {
            WakeAllPlayers();
        }
        else
        {
            if (GameSpeedBoost <= 0.0 && !AllSleeping)
                return;
            GameSpeedBoost =
                GameMath.Clamp(GameSpeedBoost + dt * (AllSleeping ? 400f : -2000f), 0.0f, 17000f);
            api.World.Calendar.SetTimeSpeedModifier("sleeping", (int)GameSpeedBoost);
        }


        if (api.World.Calendar.HourOfDay is > 7 and < 22) WakeAllPlayers();
    }

    public void WakeAllPlayers()
    {
        api.Event.PushEvent("unsleep");
        GameSpeedBoost = 0.0f;
        api.World.Calendar.SetTimeSpeedModifier("sleeping", (int)GameSpeedBoost);
        if (api.Side == EnumAppSide.Client)
        {
            var behavior = capi.World.Player.Entity.GetBehavior<EntityBehaviorTiredness>();
            if ((behavior != null ? behavior.IsSleeping ? 1 : 0 : 0) != 0)
                capi.World.Player?.Entity.TryUnmount();
            AllSleeping = false;
        }
        else
        {
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
            }

            AllSleeping = false;
        }
    }

    public bool AreAllPlayersSleeping(string bypassUID = null)
    {
        if (api.World.Calendar.HourOfDay is > 7 and < 22) return false;
        var playerSleeping = 0;
        var playerNotSleeping = 0;
        var allOnlineNotOpPlayers = (IServerPlayer[])sapi.World.AllOnlinePlayers;
        foreach (var serverPlayer in allOnlineNotOpPlayers)
            if (serverPlayer!.ConnectionState == EnumClientState.Playing &&
                serverPlayer.WorldData.CurrentGameMode != EnumGameMode.Spectator && serverPlayer.PlayerUID != bypassUID)
            {
                var behavior = serverPlayer.Entity.GetBehavior<EntityBehaviorTiredness>();
                if ((behavior != null ? behavior.IsSleeping ? 1 : 0 : 0) != 0)
                    ++playerSleeping;
                else
                    ++playerNotSleeping;
            }

        if (playerSleeping == 0) return false;

        var percentPlayerSleeping = playerSleeping / (float)(playerSleeping + playerNotSleeping);

        if (percentPlayerSleeping < 0.80)
        {
            foreach (var player in serverOpPlayerArray)
                player.SendIngameError("", $"{percentPlayerSleeping * 100}%/80% joueurs dorment");

            return false;
        }

        foreach (var player in serverOpPlayerArray)
        {
            var hour = (int)sapi.World.Calendar.HourOfDay;
            var minute = (int)((sapi.World.Calendar.HourOfDay - hour) * 60f);
            player.SendIngameError("", $"Bonne nuit: {hour:00}:{minute:00}");
        }

        return true;
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        base.StartClientSide(api);
        this.api = api;
        capi = api;
        api.Event.ReloadShader += LoadShader;
        LoadShader();
        renderer = new EyesOverlayRenderer(api, eyeShaderProg);
        api.Event.RegisterRenderer(renderer, EnumRenderStage.Ortho, "sleeping");
        api.Event.LeaveWorld += (Action)(() => renderer?.Dispose());
        clientChannel = api.Network.RegisterChannel("sleeping")
            .RegisterMessageType(typeof(NetworksMessageAllSleepMode)).SetMessageHandler(
                new NetworkServerMessageHandler<NetworksMessageAllSleepMode>(OnAllSleepingStateChanged));
    }

    public bool LoadShader()
    {
        eyeShaderProg = capi.Shader.NewShaderProgram();
        eyeShaderProg.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        eyeShaderProg.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
        eyeShaderProg.VertexShader.Code = VertexShaderCode;
        eyeShaderProg.FragmentShader.Code = FragmentShaderCode;
        capi.Shader.RegisterMemoryShaderProgram("sleepoverlay", eyeShaderProg);
        if (renderer != null)
            renderer.eyeShaderProg = eyeShaderProg;
        return eyeShaderProg.Compile();
    }

    private void OnAllSleepingStateChanged(NetworksMessageAllSleepMode networkMessage)
    {
        AllSleeping = networkMessage.On;
        if (networkMessage.On)
        {
            fastEventId ??= api.Event.RegisterGameTickListener(FastTick, 20);
            return;
        }

        capi.Event.PushEvent("unsleep");
    }
}