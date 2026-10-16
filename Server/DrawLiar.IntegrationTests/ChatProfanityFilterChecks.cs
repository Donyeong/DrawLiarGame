using System.Text;
using DrawLiar;

internal static partial class Integration
{
    private static void VerifyChatProfanityFilter()
    {
        (string Source, string Expected)[] masked =
        {
            ("씨발", "**"),
            ("시발", "**"),
            ("병신아", "**아"),
            ("개새끼", "***"),
            ("안녕 씨 발! 😀", "안녕 ***! 😀"),
            ("시.발", "***"),
            ("병-신", "***"),
            ("ㅅㅂ", "**"),
            ("ㅂㅅ", "**"),
            ("*ㅅㅂ*", "****"),
            ("ㅆㅣㅂㅏㄹ", "*****"),
            ("씨\u200B발", "***"),
            ("씨\u2060발", "***"),
            ("씨\uFEFF발", "***"),
            ("시발 점수", "** 점수"),
            ("시발.점수", "**.점수"),
            ("fuck", "****"),
            ("*fuck*", "******"),
            ("FuCk!", "****!"),
            ("shit", "****"),
            ("sh1t", "****"),
            ("5hit", "****"),
            ("sh!t", "****"),
            ("$h17", "****"),
            ("ＦＵＣＫ", "****"),
            ("𝐟𝐮𝐜𝐤", "********"),
            ("😀 𝐟𝐮𝐜𝐤 😺", "😀 ******** 😺"),
            ("씨😀발", "****"),
            ("f😀uck", "******"),
            ("f u c k", "*******"),
            ("f u c k 그림", "******* 그림"),
            ("s.h.i.t", "*******"),
            ("f\u200Buck", "*****"),
            ("fucks fucked fucker fuckers fucking", "***** ****** ****** ******* *******"),
            ("motherfuckers bullshit bitches assholes", "************* ******** ******* ********"),
            ("😀 씨발! fuck 😺", "😀 **! **** 😺"),
            ("씨발, 씨발", "**, **"),
            ("씨발씨발", "****"),
            ("좆병신", "***"),
            ("시씨발발", "시**발")
        };
        foreach (var example in masked)
        {
            string result = ChatProfanityFilter.Mask(example.Source);
            Check(result == example.Expected, "비속어와 우회 표기는 해당 원문 구간만 가려야 합니다: " + example.Source);
            Check(result.Length == example.Source.Length, "비속어 필터는 원문의 UTF-16 길이를 유지해야 합니다.");
            Check(ChatProfanityFilter.Mask(result) == result, "서버와 클라이언트에서 다시 필터링해도 결과가 같아야 합니다: " + example.Source);
        }
        foreach (string source in new[] { "씨발ㅅㅂ", "ㅅㅂ씨발", "fuckㅅㅂ", "ㅅㅂfuck" })
        {
            string result = ChatProfanityFilter.Mask(source);
            Check(ChatProfanityFilter.Mask(result) == result,
                "비속어에 붙은 초성 약어는 먼저 가린 부분 때문에 다음 필터에서 새로 노출되면 안 됩니다: " + source);
        }

        foreach (string word in new[] { "씨발", "병신", "개새끼", "좆", "좃" })
        {
            string decomposed = word.Normalize(NormalizationForm.FormD);
            Check(ChatProfanityFilter.Mask(decomposed) == new string('*', decomposed.Length),
                "분해된 한글 조합자도 원문 길이를 유지하며 가려야 합니다: " + word);
        }

        string[] unchanged =
        {
            "", " ", "안녕하세요. 좋은 그림이에요!", "사과, 배, 딸기", "18번 그림", "병원에 가요",
            "시발점", "이 사건의 시발점입니다", "시발역", "시발열차", "강아지 새끼", "졸라맨",
            "조선", "조지아", "시바견", "서양미술",
            "assistant", "class", "classic", "pass", "Scunthorpe", "Dickens", "cocktail", "shuttle",
            "😀 그림 😺", "<b>그림</b>", "***", "시**발", "f****uck",
            "\uD800", "\uDC00", "\uD800 꽃 그림 \uDC00",
            new string('가', 200)
        };
        foreach (string source in unchanged)
            Check(ChatProfanityFilter.Mask(source) == source, "정상 단어·문자·기존 마스크는 그대로 보존해야 합니다: " + source);

        foreach (string word in new[] { "조선", "조지아", "시바견", "서양미술", "시발점", "시발역", "시발열차" })
        {
            string decomposed = word.Normalize(NormalizationForm.FormD);
            Check(ChatProfanityFilter.Mask(decomposed) == decomposed,
                "분해 한글의 다음 음절 초성을 비속어의 받침으로 오인하면 안 됩니다: " + word);
        }
        foreach (string source in new[] { "ㅈㅗㅅㅓㄴ", "ㅈㅗㅈㅣㅇㅏ", "ㅅㅣㅂㅏㄱㅕㄴ", "ㅅㅓㅇㅑㅇㅁㅣㅅㅜㄹ" })
            Check(ChatProfanityFilter.Mask(source) == source, "호환 자모로 적은 정상 단어는 보존해야 합니다: " + source);

        Check(string.IsNullOrEmpty(ChatProfanityFilter.Mask(null!)), "누락된 채팅도 예외 없이 처리해야 합니다.");
        Report("채팅 비속어·영문 경계·공백/기호/숨은 문자 우회·한글 정규화·정상어·원문 길이/이모지·중복 필터 검증");
    }
}
