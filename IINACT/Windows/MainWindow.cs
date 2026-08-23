using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using FFXIV_ACT_Plugin.Config;
using RainbowMage.OverlayPlugin;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using NAudio.Wave;
using RainbowMage.OverlayPlugin.EventSources;

namespace IINACT.Windows;

public class MainWindow : Window, IDisposable
{
    private Plugin Plugin { get; }

    private int selectedOverlayIndex;

    public MainWindow(Plugin plugin) : base($"IINACT_CN v{plugin.Version}")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(307, 207),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        Plugin = plugin;
    }

    public IPluginConfig? OverlayPluginConfig { get; set; }
    public BuiltinEventConfig? OverlayPluginEventConfig { get; set; }
    public IReadOnlyList<RainbowMage.OverlayPlugin.IOverlayTemplate>? OverlayPresets { get; set; }
    private string[]? OverlayNames => OverlayPresets?.Select(x => x.Name).ToArray();
    public RainbowMage.OverlayPlugin.WebSocket.ServerController? Server { get; set; }

    public void Dispose() { }

    public override void Draw()
    {
        using var bar = ImRaii.TabBar("settingsTabs");
        if (!bar) return;

        DrawMainWindow();
        DrawParseSettings();
        DrawTtsSettings();
        DrawWebSocketSettings();
    }

    private void DrawMainWindow()
    {
        using var tab = ImRaii.TabItem("状态");
        if (!tab) return;

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "OverlayPlugin 状态：");
        ImGuiHelpers.ScaledRelativeSameLine(155);
        ImGui.Text(Plugin.OverlayPluginStatus);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(ImGuiColors.DalamudGrey, "悬浮窗地址生成器：");

        var comboWidth = ImGui.GetWindowWidth() * 0.8f;
        
        var selectedIndexOverlayName = OverlayNames?[selectedOverlayIndex] ?? "";
        var selectedOverlayName = Plugin.Configuration.SelectedOverlay ?? selectedIndexOverlayName;
        if (selectedOverlayName != selectedIndexOverlayName)
            for (var i = 0; i < OverlayNames?.Length; i++)
                if (OverlayNames?[i] == selectedOverlayName) 
                    selectedOverlayIndex = i;
        
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.BeginCombo("悬浮窗预设##overlay", selectedOverlayName))
        {
            for (var i = 0; i < OverlayNames?.Length; i++)
            {
                var currentOverlayName = OverlayNames?[i] ?? "";
                if (ImGui.Selectable(currentOverlayName, currentOverlayName == selectedOverlayName))
                {
                    selectedOverlayIndex = i;
                    Plugin.Configuration.SelectedOverlay = currentOverlayName;
                    Plugin.Configuration.Save();
                }
            }

            ImGui.EndCombo();
        }

        var selectedOverlay = OverlayPresets?[selectedOverlayIndex];
        Uri.TryCreate($"ws://{Server?.Address}:{Server?.Port}/ws", UriKind.Absolute, out var webSocketServer);
        var overlayUri = selectedOverlay?.ToOverlayUri(webSocketServer);
        var overlayUriString = overlayUri?.ToString() ?? "<生成地址时出错>";

        ImGui.SetNextItemWidth(comboWidth);
        ImGui.InputText("地址##overlayUri", ref overlayUriString, 1000, ImGuiInputTextFlags.ReadOnly);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        var serverStatus = Server is null ? "正在初始化…" : "已停止";

        if (Server?.Running ?? false)
            serverStatus = $"正在监听 {Server?.Address}:{Server?.Port}";

        if (Server?.Failed ?? false)
        {
            serverStatus = Server.LastException?.Message ?? "失败";
            if (Server.LastException is SocketException { ErrorCode: 10048 })
                serverStatus = $"端口 {Server?.Port} 已被占用";
        }

        ImGui.TextColored(ImGuiColors.DalamudGrey, "WebSocket 服务器：");
        ImGuiHelpers.ScaledRelativeSameLine(155);
        ImGui.Text(serverStatus);
        ImGui.GetWindowDpiScale();

        if (Server?.Running ?? false)
        {
            if (ImGui.Button("停止"))
                Server.Stop();

            ImGui.SameLine();

            if (ImGui.Button("重启"))
                Server.Restart();
        }
        else if (Server is not null)
        {
            if (ImGui.Button("启动"))
                Server.Start();
        }
    }

     private void DrawParseSettings()
    {
        using var tab = ImRaii.TabItem("解析器");
        if (!tab) return;

        ImGui.Spacing();
        var elementWidth = ImGui.GetWindowWidth() - (150 * ImGuiHelpers.GlobalScale);
        var logFilePath = Plugin.Configuration.LogFilePath;
        ImGui.SetNextItemWidth(elementWidth);
        ImGui.InputText("日志文件夹##logFilePath", ref logFilePath, 200, ImGuiInputTextFlags.ReadOnly);
        ImGui.SameLine();
        if (ImGuiComponents.DisabledButton(FontAwesomeIcon.Folder))
        {
            Plugin.FileDialogManager.OpenFolderDialog("选择保存日志的文件夹", (success, path) =>
            {
                if (!success) return;
                Plugin.Configuration.LogFilePath = path;
                Plugin.Configuration.Save();
            }, Plugin.Configuration.LogFilePath);
        }
        ImGui.Spacing();
        ImGui.SetNextItemWidth(elementWidth);
        var selectedParseFilter = (ParseFilterMode)Plugin.Configuration.ParseFilterMode;
        if (ImGui.BeginCombo("解析过滤##parseFilter", GetParseFilterDisplayName(selectedParseFilter)))
        {
            foreach (var filter in Enum.GetValues<ParseFilterMode>())
                if (ImGui.Selectable(GetParseFilterDisplayName(filter), selectedParseFilter == filter))
                {
                    Plugin.Configuration.ParseFilterMode = (int)filter;
                    Plugin.Configuration.Save();
                }

            ImGui.EndCombo();
        }

        ImGui.Spacing();
        
        var writeLogFile = Plugin.Configuration.WriteLogFile;
        if (ImGui.Checkbox("写入网络日志文件", ref writeLogFile))
        {
            Plugin.Configuration.WriteLogFile = writeLogFile;
            Plugin.Configuration.Save();
        }

        var disablePvp = Plugin.Configuration.DisablePvp;
        if (ImGui.Checkbox("在 PvP 中停止写入网络日志文件", ref disablePvp))
        {
            if (Plugin.ClientState.IsPvP && disablePvp) Plugin.Configuration.DisableWritingPvpLogFile = true;

            Plugin.Configuration.DisablePvp = disablePvp;
            Plugin.Configuration.Save();
        }

        var logChatMessages = Plugin.Configuration.LogChatMessages;
        if (ImGui.Checkbox("在日志文件中包含聊天与回显消息", ref logChatMessages))
        {
            Plugin.Configuration.LogChatMessages = logChatMessages;
            Plugin.SetChatMessageLoggingEnabled(logChatMessages);
            Plugin.Configuration.Save();
        }

        var autoDeleteNetworkLogs = Plugin.Configuration.AutoDeleteNetworkLogs;
        if (ImGui.Checkbox("自动删除旧网络日志文件", ref autoDeleteNetworkLogs))
        {
            Plugin.Configuration.AutoDeleteNetworkLogs = autoDeleteNetworkLogs;
            Plugin.Configuration.Save();
        }

        if (autoDeleteNetworkLogs)
        {
            var networkLogRetentionDays = Plugin.Configuration.NetworkLogRetentionDays;
            ImGui.Text("删除早于");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(30 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("天##networkLogRetentionDays", ref networkLogRetentionDays))
            {
                Plugin.Configuration.NetworkLogRetentionDays = Math.Clamp(networkLogRetentionDays, 1, 3650);
                Plugin.Configuration.Save();
            }
        }

        var disableDamageShield = Plugin.Configuration.DisableDamageShield;
        if (ImGui.Checkbox("禁用伤害护盾估算", ref disableDamageShield))
        {
            Plugin.Configuration.DisableDamageShield = disableDamageShield;
            Plugin.Configuration.Save();
        }

        var disableCombinePets = Plugin.Configuration.DisableCombinePets;
        if (ImGui.Checkbox("不合并宠物与主人", ref disableCombinePets))
        {
            Plugin.Configuration.DisableCombinePets = disableCombinePets;
            Plugin.Configuration.Save();
        }

        var endEncounterOutOfCombat = OverlayPluginEventConfig?.EndEncounterOutOfCombat ?? true;
        if (ImGui.Checkbox("离开战斗后自动结束本次战斗", ref endEncounterOutOfCombat))
        {
            if (OverlayPluginEventConfig is not null)
            {
                OverlayPluginEventConfig.EndEncounterOutOfCombat = endEncounterOutOfCombat;
                if (OverlayPluginConfig is not null)
                {
                    OverlayPluginEventConfig.SaveConfig(OverlayPluginConfig);
                    OverlayPluginConfig.Save();
                }
            }
        }

        var showDebug = Plugin.Configuration.ShowDebug;
        if (ImGui.Checkbox("显示调试选项", ref showDebug))
        {
            Plugin.Configuration.ShowDebug = showDebug;
            Plugin.Configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var playerCharacterName = Plugin.Configuration.PlayerCharacterName;
        ImGui.SetNextItemWidth(elementWidth);
        if (ImGui.InputText("玩家名称", ref playerCharacterName, 100))
        {
            Plugin.Configuration.PlayerCharacterName = playerCharacterName;
            Plugin.Configuration.Save();
        }

        if (!showDebug) return;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var simulateIndividualDoTCrits = Plugin.Configuration.SimulateIndividualDoTCrits;
        if (ImGui.Checkbox("模拟独立 DoT 暴击", ref simulateIndividualDoTCrits))
        {
            Plugin.Configuration.SimulateIndividualDoTCrits = simulateIndividualDoTCrits;
            Plugin.Configuration.Save();
        }

        var showRealDoTTicks = Plugin.Configuration.ShowRealDoTTicks;
        if (ImGui.Checkbox("同时显示“真实”DoT 跳字", ref showRealDoTTicks))
        {
            Plugin.Configuration.ShowRealDoTTicks = showRealDoTTicks;
            Plugin.Configuration.Save();
        }
    }

    private void DrawTtsSettings()
    {
        using var tab = ImRaii.TabItem("文本转语音");
        if (!tab) return;
        
        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "Google TTS：");
        ImGui.Spacing();

        var forceGoogleTts = Plugin.Configuration.ForceGoogleTts;
        if (ImGui.Checkbox("使用 Google TTS，替代 SAPI", ref forceGoogleTts))
        {
            Plugin.Configuration.ForceGoogleTts = forceGoogleTts;
            Plugin.Configuration.Save();
        }

        ImGui.Spacing();

        var googleTtsLanguage = Plugin.Configuration.GoogleTtsLanguage;
        ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputText("语言", ref googleTtsLanguage, 10))
        {
            Plugin.Configuration.GoogleTtsLanguage = googleTtsLanguage;
            Plugin.Configuration.Save();
        }
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "（例如：ja、en、de、fr、ko）");
        ImGui.Spacing();

        var ttsDeviceCount = WaveOut.DeviceCount;
        var currentDevice = Plugin.Configuration.TtsPlaybackDevice;
        var currentDeviceName = currentDevice == -1 ? "默认" : WaveOut.GetCapabilities(currentDevice).ProductName;
        
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);

        if (ImGui.BeginCombo("播放设备", currentDeviceName))
        {
            if (ImGui.Selectable("默认", currentDevice == -1))
            {
                Plugin.Configuration.TtsPlaybackDevice = -1;
                Plugin.Configuration.Save();
            }

            for (var i = 0; i < ttsDeviceCount; i++)
            {
                var caps = WaveOut.GetCapabilities(i);
                if (ImGui.Selectable(caps.ProductName, currentDevice == i))
                {
                    Plugin.Configuration.TtsPlaybackDevice = i;
                    Plugin.Configuration.Save();
                }
            }

            ImGui.EndCombo();
        }
    }

    private void DrawWebSocketSettings()
    {
        using var tab = ImRaii.TabItem("WebSocket 服务器");
        if (!tab) return;
        
        ImGui.Spacing();
        var wsServerIp = OverlayPluginConfig?.WSServerIP ?? "";
        ImGui.InputText("IP 地址", ref wsServerIp, 100, ImGuiInputTextFlags.None);

        if (IPAddress.TryParse(wsServerIp, out var address))
        {
            if (OverlayPluginConfig is not null)
                OverlayPluginConfig.WSServerIP = address.ToString();
        }
        else if (wsServerIp == "*")
        {
            if (OverlayPluginConfig is not null)
                OverlayPluginConfig.WSServerIP = "*";
        }

        var wsServerPort = OverlayPluginConfig?.WSServerPort.ToString() ?? "";
        ImGui.InputText("端口", ref wsServerPort, 100, ImGuiInputTextFlags.None);

        if (int.TryParse(wsServerPort, out var port))
        {
            if (OverlayPluginConfig is not null)
                OverlayPluginConfig.WSServerPort = port;
        }

        OverlayPluginConfig?.Save();
    }

    private static string GetParseFilterDisplayName(ParseFilterMode filter)
    {
        return filter.ToString() switch
        {
            "None" => "不过滤",
            "Party" => "仅队伍",
            "Alliance" => "仅团队",
            "All" => "全部",
            _ => filter.ToString()
        };
    }

}
