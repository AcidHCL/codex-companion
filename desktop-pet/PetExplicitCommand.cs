using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace DesktopPet;

// Only complete, short commands from the current user enter this fast path.
// Do not extract commands from prose, quotes, links, model output or history.
internal static class PetExplicitCommand
{
    private const string Prefix = @"(?:(?:小?deepseek|鲸鲸|大肥鱼|肥鱼)[，,]?)?(?:(?:请你|请|麻烦你|帮我|你能不能|你可不可以|你能|你可以|能不能|可不可以|能否|可以|能|我想听你|你))?(?:现在)?(?:给我)?";
    private const string Suffix = @"(?:给我听听?|听听?)?(?:吧|呀|啊|啦|嘛|吗|好不好|好吗|可以吗)?";
    private const string BangyouName = @"(?:《邦友的酒》|邦友的酒|邦友|保持q弹|q弹(?:转转舞)?|转转舞)";
    private static readonly Regex Bangyou = new(@"\A" + Prefix + @"(?:(?:播放|放|唱|跳|表演)(?:一?首|一?段|一下|一遍|个)?|来(?:一?首|一?段|个))" + BangyouName + Suffix + @"\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private static readonly Regex StopBangyou = new(@"\A" + Prefix + @"(?:停(?:止|下)(?:播放|表演|唱|跳)?" + BangyouName + @"(?:了)?|(?:别(?:再)?|不要(?:再)?)(?:播放|表演|唱|跳)" + BangyouName + @"了)(?:吧|呀|啊|啦|好吗)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private static readonly Regex StopLetMeGo = new(@"\A" + Prefix + @"(?:停(?:止|下)(?:播放|唱)?(?:letmego|《letmego》)(?:了)?|(?:别(?:再)?|不要(?:再)?)唱(?:letmego|《letmego》)了)(?:吧|呀|啊|啦|好吗)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Sing = new(@"\A" + Prefix + @"(?:唱(?:一?首歌|个歌|歌|一下歌|(?:一?首|一下)?(?:letmego|《letmego》))|来(?:一?首歌|一曲|一?首letmego)|开始唱歌|播放(?:一下)?(?:letmego|《letmego》))" + Suffix + @"\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Stop = new(@"\A" + Prefix + @"(?:停止唱歌|停止播放letmego|停止播放《letmego》|不要再唱(?:歌)?了|别唱(?:歌)?了|别再唱(?:歌)?了|不要唱(?:歌)?了|不唱了|停(?:止|下)唱歌)" + @"(?:吧|呀|啊|啦|好吗)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));

    internal static PetBehaviorCommand? Parse(string text, out string? stopTarget)
    {
        stopTarget = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 120 || text.IndexOfAny(new[] { '\r', '\n', '"', '\'', '“', '”', '‘', '’', '`', '：', ':', '>', '<' }) >= 0)
            return null;
        string value = string.Concat(text.Trim().Where(character => !char.IsWhiteSpace(character))).TrimEnd('。', '.', '！', '!', '？', '?', '~', '～');
        if (StopBangyou.IsMatch(value)) { stopTarget = "bangyou-dance"; return new("stop_performance"); }
        if (StopLetMeGo.IsMatch(value)) { stopTarget = "sing"; return new("stop_performance"); }
        if (Stop.IsMatch(value)) return new("stop_performance");
        if (Bangyou.IsMatch(value)) return new("play_performance", Name: "bangyou-dance");
        if (Sing.IsMatch(value)) return new("play_performance", Name: "sing");
        return null;
    }
}
