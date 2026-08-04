using DalamudActCompat.ActRuntime;

namespace DalamudActCompat.UI;

internal static class ActCapabilityDisplay
{
    public static string Label(ActCapability capability, UiText text) => capability switch
    {
        ActCapability.ReadCombatLogs => text.Get("读取战斗日志", "Read combat logs"),
        ActCapability.ReadLocalConfiguration => text.Get("读取本地配置", "Read local configuration"),
        ActCapability.TextToSpeech => text.Get("文本转语音", "Text to speech"),
        ActCapability.Clipboard => text.Get("访问剪贴板", "Clipboard access"),
        ActCapability.NetworkRequest => text.Get("网络请求", "Network requests"),
        ActCapability.LaunchExternalProcess => text.Get("启动外部程序", "Launch external processes"),
        ActCapability.WriteFiles => text.Get("写入文件", "Write files"),
        ActCapability.GameCommand => text.Get("发送游戏指令", "Send game commands"),
        ActCapability.NativeGameMemory => text.Get("访问游戏原生内存", "Access native game memory"),
        ActCapability.HighRiskScript => text.Get("运行高风险脚本", "Run high-risk scripts"),
        _ => capability.ToString(),
    };
}
