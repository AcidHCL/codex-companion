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
        "token-come", "bowl-tap", "bowl-tap-eager", "dance", "dance-2", "dance-3", "fireworks", "bangyou-dance"
    };
    internal static IReadOnlyList<string> Installed(SceneAnimationPack? pack) =>
        pack == null ? System.Array.Empty<string>() : Names.Where(pack.Actions.ContainsKey).ToArray();
    internal static bool IsMusicPerformance(string? name) => name is "sing" or "bangyou-dance";
    internal static string Label(string name) => name switch
    {
        "sing" => L.T("唱 Let Me Go", "Sing Let Me Go"), "bangyou-dance" => L.T("保持 Q 弹 · 邦友的酒", "Bouncy spin · Bangyou de Jiu"),
        "happy" => L.T("开心 · 得意", "Happy · Proud"), "angry" => L.T("生气 · 鼓脸", "Angry · Pouting"),
        "startled" => L.T("受惊 · 回神", "Startled · Recovering"), "bashful" => L.T("害羞 · 嘴硬", "Shy · Acting proud"),
        "token-come" => L.T("招手讨 Token", "Waving for tokens"), "bowl-tap" => L.T("敲碗等饭", "Bowl taps for dinner"), "bowl-tap-eager" => L.T("木勺敲锅", "Wooden spoon taps"),
        "dance" => L.T("小拳踏步舞", "Little-fist stepping dance"), "dance-2" => L.T("摆臂摇尾舞", "Arm-swing and tail dance"), "dance-3" => L.T("举手踢腿舞", "Hands-up kicking dance"), "fireworks" => L.T("烟花小剧场", "Fireworks show"), _ => name
    };

    // Intent hints are only exposed when the same action is installed. Keep
    // artwork names separate from the physical movement tool vocabulary.
    internal static string Intent(string name) => name switch
    {
        "bangyou-dance" => L.T("邦友的酒、邦友、保持Q弹、Q弹、转转舞；指定这首歌或这段舞时选择这个，配套音乐播放完后结束", "Bangyou de Jiu, Bangyou, bouncy spin or spinning dance; choose this for the named song or dance, ending when its music finishes"),
        "happy" => L.T("开心、得意、小小庆祝", "happy, proud, a little celebration"),
        "angry" => L.T("轻微生气、鼓脸、被逗弄时符合当前角色的俏皮抗议", "mild anger, pouting, playful protests when teased that fit the character"),
        "startled" => L.T("受惊后回神、突然被碰到的惊讶", "recovering after a startle, surprise at an unexpected touch"),
        "bashful" => L.T("害羞、被夸奖后的嘴硬不好意思", "shyness, acting proud but embarrassed after praise"),
        "token-come" => L.T("招手讨 Token、Token 来、口粮来、招手讨口粮的俏皮表演", "waving for tokens, bring tokens or food, a playful begging-for-food performance"),
        "bowl-tap" => L.T("敲碗等饭、等开饭、轻轻催饭", "tapping the bowl, waiting for dinner, gently asking for food"),
        "bowl-tap-eager" => L.T("木勺敲锅、拿勺敲锅、急急催饭", "tapping a pot with a wooden spoon, eagerly asking for food"),
        "dance" => L.T("小拳踏步舞、握小拳头踏步；只说跳舞且未指定舞蹈版本时选择这个", "little-fist stepping dance; choose this when the user asks to dance without a specific version"),
        "dance-2" => L.T("摆臂摇尾舞、摆动手臂摇尾巴、第二段舞、跳舞 2", "arm-swing and tail dance, second dance, dance 2"),
        "dance-3" => L.T("举手踢腿舞、举手踢腿、第三段舞、跳舞 3", "hands-up kicking dance, third dance, dance 3"),
        "fireworks" => L.T("烟花小剧场、烟花庆祝、放烟花", "fireworks show, fireworks celebration, set off fireworks"),
        _ => ""
    };
}
