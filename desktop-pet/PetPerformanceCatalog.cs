using System.Collections.Generic;
using System.Linq;

namespace DesktopPet;

// Expressions are performances. Physical states are deliberately absent from
// this registry, even when their assets can be previewed in the library.
internal static class PetPerformanceCatalog
{
    internal static readonly string[] Names =
    {
        "sing", "happy", "angry", "startled", "bashful",
        "token-come", "bowl-tap", "bowl-tap-eager", "dance", "dance-2", "dance-3", "fireworks"
    };
    internal static IReadOnlyList<string> Installed(SceneAnimationPack? pack) =>
        pack == null ? System.Array.Empty<string>() : Names.Where(pack.Actions.ContainsKey).ToArray();
    internal static string Label(string name) => name switch
    {
        "sing" => "唱 Let Me Go", "happy" => "开心 · 得意", "angry" => "生气 · 鼓脸",
        "startled" => "受惊 · 回神", "bashful" => "害羞 · 嘴硬",
        "token-come" => "招手讨 Token", "bowl-tap" => "敲碗等饭", "bowl-tap-eager" => "木勺敲锅",
        "dance" => "小拳踏步舞", "dance-2" => "摆臂摇尾舞", "dance-3" => "举手踢腿舞", "fireworks" => "烟花小剧场", _ => name
    };

    // Intent hints are only exposed when the same action is installed. Keep
    // artwork names separate from the physical movement tool vocabulary.
    internal static string Intent(string name) => name switch
    {
        "happy" => "开心、得意、小小庆祝",
        "angry" => "轻微生气、鼓脸、被叫肥鱼时俏皮抗议",
        "startled" => "受惊后回神、突然被碰到的惊讶",
        "bashful" => "害羞、被夸奖后的嘴硬不好意思",
        "token-come" => "招手讨 Token、Token 来、口粮来、招手讨口粮的俏皮表演",
        "bowl-tap" => "敲碗等饭、等开饭、轻轻催饭",
        "bowl-tap-eager" => "木勺敲锅、拿勺敲锅、急急催饭",
        "dance" => "小拳踏步舞、握小拳头踏步；只说跳舞且未指定舞蹈版本时选择这个",
        "dance-2" => "摆臂摇尾舞、摆动手臂摇尾巴、第二段舞、跳舞 2",
        "dance-3" => "举手踢腿舞、举手踢腿、第三段舞、跳舞 3",
        "fireworks" => "烟花小剧场、烟花庆祝、放烟花",
        _ => ""
    };
}
