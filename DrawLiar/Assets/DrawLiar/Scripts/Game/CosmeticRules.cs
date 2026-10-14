#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace DrawLiar
{
    public static class CosmeticRules
    {
        public const string NICKNAME_EFFECT = "NicknameEffect";
        public const string AVATAR_BACKGROUND = "AvatarBackground";
        public const string AVATAR_FRAME = "AvatarFrame";
        public const string CHAT_BUBBLE = "ChatBubble";
        public static readonly string[] Categories = { NICKNAME_EFFECT, AVATAR_BACKGROUND, AVATAR_FRAME, CHAT_BUBBLE };

        private static readonly CommerceProduct[] _products =
        {
            Product("nickname-prism", "프리즘 잉크", NICKNAME_EFFECT, "무지개빛으로 닉네임을 물들여요.", 80),
            Product("nickname-gold", "황금 서명", NICKNAME_EFFECT, "따뜻한 황금빛으로 닉네임을 꾸며요.", 100),
            Product("nickname-mint", "민트 잉크", NICKNAME_EFFECT, "산뜻한 민트빛으로 닉네임을 꾸며요.", 120),
            Product("nickname-neon", "네온 사인", NICKNAME_EFFECT, "선명한 네온빛으로 닉네임을 꾸며요.", 140),
            Product("nickname-sunset", "노을 잉크", NICKNAME_EFFECT, "분홍빛 노을 색으로 닉네임을 꾸며요.", 160),
            Product("background-aurora", "오로라 정원", AVATAR_BACKGROUND, "캐릭터 뒤로 오로라빛이 펼쳐져요.", 80),
            Product("background-starlight", "별빛 밤하늘", AVATAR_BACKGROUND, "캐릭터 뒤로 작은 별들이 빛나요.", 100),
            Product("background-petals", "벚꽃 편지", AVATAR_BACKGROUND, "캐릭터 뒤를 분홍 꽃잎으로 꾸며요.", 120),
            Product("background-ocean", "푸른 파도", AVATAR_BACKGROUND, "캐릭터 뒤로 푸른 물결이 펼쳐져요.", 140),
            Product("background-sunset", "노을 하늘", AVATAR_BACKGROUND, "캐릭터 뒤를 따뜻한 노을로 꾸며요.", 160),
            Product("frame-laurel", "월계수 테두리", AVATAR_FRAME, "캐릭터를 월계수 잎으로 감싸요.", 80),
            Product("frame-crystal", "크리스탈 테두리", AVATAR_FRAME, "캐릭터를 반짝이는 보석으로 감싸요.", 100),
            Product("frame-neon", "네온 테두리", AVATAR_FRAME, "캐릭터를 선명한 네온 선으로 감싸요.", 120),
            Product("frame-ribbon", "리본 테두리", AVATAR_FRAME, "캐릭터를 사랑스러운 리본으로 감싸요.", 140),
            Product("frame-stars", "별자리 테두리", AVATAR_FRAME, "캐릭터를 작은 별자리로 감싸요.", 160),
            Product("bubble-cloud", "구름 말풍선", CHAT_BUBBLE, "채팅을 포근한 구름 모양으로 꾸며요.", 80),
            Product("bubble-memo", "메모지 말풍선", CHAT_BUBBLE, "채팅을 노란 메모지로 꾸며요.", 100),
            Product("bubble-neon", "네온 말풍선", CHAT_BUBBLE, "채팅을 선명한 네온 색으로 꾸며요.", 120),
            Product("bubble-pixel", "픽셀 말풍선", CHAT_BUBBLE, "채팅을 귀여운 픽셀 모양으로 꾸며요.", 140),
            Product("bubble-petal", "꽃잎 말풍선", CHAT_BUBBLE, "채팅을 부드러운 꽃잎 색으로 꾸며요.", 160)
        };
        private static readonly Dictionary<string, CommerceProduct> _productsById =
            _products.ToDictionary(product => product.Id, StringComparer.Ordinal);

        public static CommerceProduct[] CreateProducts() => _products.Select(product => new CommerceProduct
        {
            Id = product.Id, Name = product.Name, Kind = product.Kind, Category = product.Category,
            Description = product.Description, PricePaidGems = product.PricePaidGems
        }).ToArray();

        private static CommerceProduct Product(string id, string name, string category, string description, int price) =>
            new CommerceProduct { Id = id, Name = name, Kind = CommerceRules.COSMETIC, Category = category,
                Description = description, PricePaidGems = price };

        public static CommerceProduct Find(string productId) => productId != null && _productsById.TryGetValue(productId, out var product) ? product : null;
        public static bool IsCategory(string category) => Categories.Contains(category);

        public static string GetEquipped(CosmeticLoadout loadout, string category)
        {
            if (loadout == null) return "";
            switch (category)
            {
                case NICKNAME_EFFECT: return loadout.NicknameEffect ?? "";
                case AVATAR_BACKGROUND: return loadout.AvatarBackground ?? "";
                case AVATAR_FRAME: return loadout.AvatarFrame ?? "";
                case CHAT_BUBBLE: return loadout.ChatBubble ?? "";
                default: return "";
            }
        }

        public static CosmeticLoadout SetEquipped(CosmeticLoadout loadout, string category, string productId)
        {
            if (!IsCategory(category)) throw new ArgumentException("꾸미기 카테고리가 올바르지 않습니다.", nameof(category));
            productId = productId ?? "";
            if (productId.Length > 0 && Find(productId)?.Category != category)
                throw new ArgumentException("꾸미기 상품이 카테고리와 일치하지 않습니다.", nameof(productId));
            var result = Sanitize(loadout);
            switch (category)
            {
                case NICKNAME_EFFECT: result.NicknameEffect = productId; break;
                case AVATAR_BACKGROUND: result.AvatarBackground = productId; break;
                case AVATAR_FRAME: result.AvatarFrame = productId; break;
                case CHAT_BUBBLE: result.ChatBubble = productId; break;
            }
            return result;
        }

        public static CosmeticLoadout Sanitize(CosmeticLoadout loadout) => new CosmeticLoadout
        {
            NicknameEffect = ValidEquipped(loadout, NICKNAME_EFFECT),
            AvatarBackground = ValidEquipped(loadout, AVATAR_BACKGROUND),
            AvatarFrame = ValidEquipped(loadout, AVATAR_FRAME),
            ChatBubble = ValidEquipped(loadout, CHAT_BUBBLE)
        };

        private static string ValidEquipped(CosmeticLoadout loadout, string category)
        {
            string id = GetEquipped(loadout, category);
            return Find(id)?.Category == category ? id : "";
        }

        public static string[] SanitizeOwned(IEnumerable<string> owned) => (owned ?? Array.Empty<string>())
            .Where(id => Find(id) != null).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        public static CosmeticLoadout KeepOwned(CosmeticLoadout loadout, IEnumerable<string> owned)
        {
            var result = Sanitize(loadout);
            var ids = new HashSet<string>(SanitizeOwned(owned), StringComparer.Ordinal);
            foreach (string category in Categories)
                if (!ids.Contains(GetEquipped(result, category))) result = SetEquipped(result, category, "");
            return result;
        }
    }
}
