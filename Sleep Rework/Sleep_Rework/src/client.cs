using System;
using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Custom_Sleep;

public class client : ModSystem
{
    private ICoreClientAPI capi;
    private IClientNetworkChannel clientChannel;
    private long? clientTickId;
    private bool clientTickShoudStop;
    private IShaderProgram eyeShaderProg;

    public string FragmentShaderCode =
        "\r\n#version 330 core\r\n\r\nin vec2 uv;\r\n\r\nout vec4 outColor;\r\n\r\nuniform float level;\r\n\r\nvoid main () {\r\n    vec2 uvOffseted = vec2(uv.x - 0.5, 2 * (1 + 2*level) * (uv.y - 0.5));\r\n\tfloat strength = 1 - smoothstep(1.1 - level, 0, length(uvOffseted));\r\n\toutColor = vec4(0, 0, 0, min(1, (4 * level - 0.8) + level * strength));\r\n}\r\n";

    private float GameSpeedBoost;
    private EyesOverlayRenderer renderer;
    private bool serverInSleepingMod;
    private float sleepLevel;

    public string VertexShaderCode =
        "\r\n#version 330 core\r\n#extension GL_ARB_explicit_attrib_location: enable\r\n\r\nlayout(location = 0) in vec3 vertex;\r\n\r\nout vec2 uv;\r\n\r\nvoid main(void)\r\n{\r\n    gl_Position = vec4(vertex.xy, 0, 1);\r\n    uv = (vertex.xy + 1.0) / 2.0;\r\n}\r\n";


    public override bool ShouldLoad(EnumAppSide side)
    {
        return side == EnumAppSide.Client;
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;

        api.Event.RegisterEventBusListener(clientSleep, 0.5d, "sleepClient");
        api.Event.RegisterEventBusListener(clientUnSleep, 0.5d, "unsleepClient");

        //base.StartClientSide(api);
        api.Event.ReloadShader += LoadShader;
        LoadShader();
        renderer = new EyesOverlayRenderer(api, eyeShaderProg);
        api.Event.RegisterRenderer(renderer, EnumRenderStage.Ortho, "sleeping");
        api.Event.LeaveWorld += (Action)(() => renderer?.Dispose());

        clientChannel = api.Network.RegisterChannel("sleeping")
            .RegisterMessageType(typeof(NetworksMessageAllSleepMode)).SetMessageHandler(
                new NetworkServerMessageHandler<NetworksMessageAllSleepMode>(serverInSleepingModChanged));
    }

    private void clientSleep(string eventname, ref EnumHandling handling, IAttribute data)
    {
        capi.Logger.Debug("Client just sleep");
        clientTickId ??= capi.Event.RegisterGameTickListener(ClientTick, 20);
    }

    private void clientUnSleep(string eventname, ref EnumHandling handling, IAttribute data)
    {
        capi.Logger.Debug("Client just unsleep");

        clientTickShoudStop = true;

        //if (clientTickId != null) capi.Event.UnregisterGameTickListener((long)clientTickId);
        //clientTickId = null;
        //capi.TriggerIngameError(this, "", "");
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

    private void serverInSleepingModChanged(NetworksMessageAllSleepMode networkMessage)
    {
        serverInSleepingMod = networkMessage.On;
        capi.Logger.Debug($"Le sleepmode sur server est maintenant: {serverInSleepingMod}");
        if (networkMessage.On)
        {
            clientTickId ??= capi.Event.RegisterGameTickListener(ClientTick, 20);
            return;
        }

        if (clientTickId != null) clientTickShoudStop = true;
    }

    private void ClientTick(float dt)
    {
        capi.Logger.Debug(
            $"sleepLevel: {sleepLevel}, serverInSleepingMod: {serverInSleepingMod}, clientTickShoudStop: {clientTickShoudStop}, GameSpeedBoost: {GameSpeedBoost}");
        if (!clientTickShoudStop && !serverInSleepingMod && capi.World.Calendar.HourOfDay is > 7 and < 22)
        {
            capi.TriggerIngameError(this, "nottiredenough", Lang.Get("not-tired-enough"));
            return;
        }

        renderer.Level = sleepLevel;
        var flag = capi.World?.Player?.Entity?.MountedOn is BlockEntityBed;
        sleepLevel = GameMath.Clamp(sleepLevel + dt * (!flag || !serverInSleepingMod ? -0.35f : 0.1f), 0.0f,
            0.99f);

        if (serverInSleepingMod && capi.World.Config.GetString("temporalStormSleeping", "0").ToInt() == 0 &&
            capi.ModLoader.GetModSystem<SystemTemporalStability>().StormStrength > 0.0)
        {
            WakeClientPlayers();
        }
        else
        {
            if (clientTickShoudStop && GameSpeedBoost == 0 && sleepLevel == 0 && !serverInSleepingMod)
            {
                Debug.Assert(clientTickId != null, nameof(clientTickId) + " != null");
                capi.Event.UnregisterGameTickListener((long)clientTickId);
                clientTickId = null;
                capi.World.Calendar.SetTimeSpeedModifier("sleeping", 0);
                capi.TriggerIngameError(this, "", "");
                WakeClientPlayers();
                clientTickShoudStop = false;
                return;
            }

            if (GameSpeedBoost <= 0.0 && !serverInSleepingMod) return;

            GameSpeedBoost = GameMath.Clamp(GameSpeedBoost + dt * (serverInSleepingMod ? 400f : -2000f), 0.0f, 17000f);
            capi.World.Calendar.SetTimeSpeedModifier("sleeping", (int)GameSpeedBoost);
        }
    }

    private void WakeClientPlayers()
    {
        var behavior = capi.World.Player.Entity.GetBehavior<EntityBehaviorTiredness>();
        if ((behavior != null ? behavior.IsSleeping ? 1 : 0 : 0) != 0)
            capi.World.Player?.Entity.TryUnmount();
    }
}