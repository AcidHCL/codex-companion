using System.Collections.Generic;
using System.Linq;

namespace DesktopPet;

// This is the model's context projection, not the archived or displayed answer.
// Keep failed retrieval claims out of future prompts without altering user history.
internal static class PetWebAnswerContext
{
    internal static string Remember(string answer, IReadOnlyList<PetWebSource> sources, string? warning)
    {
        bool hasWarning = !string.IsNullOrWhiteSpace(warning);
        if (sources.Count == 0)
        {
            if (!hasWarning) return answer;
            return L.T("【历史联网状态】该轮联网未取得可用资料，旧答复未经核实，已省略其内容。不得引用其中的数值或结论；实时信息需要重新查询。\n当时联网提示：", "[Historical web status] That turn retrieved no usable evidence. Its unverified answer has been omitted. Do not reuse its values or conclusions; live information requires a new query.\nPrevious web notice: ") + warning;
        }

        string remembered = L.T("【历史助手答复】以下来源仅属于当时该轮，不是本轮检索结果，不能据此认定当前实时事实。\n", "[Historical assistant reply] These sources belong only to that earlier turn, not the current retrieval, and cannot establish current live facts.\n") + answer
            + L.T("\n历史网页来源（编号仅在该历史轮次有效）：\n", "\nHistorical web sources (numbers apply only to that earlier turn):\n")
            + string.Join("\n", sources.Select((source, index) => $"[{index + 1}] {source.Title} {source.Url}"));
        if (hasWarning) remembered += L.T("\n当时联网提示：", "\nPrevious web notice: ") + warning;
        return remembered;
    }

    internal static void ApplyMetadata(PetWebChatUpdate update, ref IReadOnlyList<PetWebSource> sources, ref string? warning)
    {
        if (update.Sources != null) sources = update.Sources;
        // A final null warning means the completed retrieval has no warning;
        // interim null values merely omit a metadata update.
        if (update.Done || update.Warning != null) warning = update.Warning;
    }
}
