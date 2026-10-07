using System;
using System.Collections.Generic;

namespace DrawLiar
{
    [Flags]
    public enum AvatarAccessory : long
    {
        None = 0, Beret = 1, Brush = 2, Crown = 4, WizardHat = 8, Headphones = 16,
        RoundGlasses = 32, Sunglasses = 64, Scarf = 128, Cape = 256, BowTie = 512,
        StarWand = 1024, Palette = 2048, Painter = Beret | Brush,
        Wink = 4096, Happy = 8192, Sleepy = 16384, Surprised = 32768, Determined = 65536,
        PainterApron = 131072, StripedShirt = 262144, PolkaDotShirt = 524288, Overalls = 1048576, StarSweater = 2097152,
        RoundBody = 4194304, SoftSquareBody = 8388608, DropletBody = 16777216,
        BeanBody = 33554432, CatBody = 67108864, BearBody = 134217728,
        Blush = 268435456, Mischievous = 536870912, Tearful = 1073741824,
        HeartEyes = 2147483648L, StarEyes = 4294967296L, SpiralEyes = 8589934592L,
        PixelFace = 17179869184L, SkullFace = 34359738368L, CatFace = 68719476736L,
        SparkleFace = 137438953472L, MellowFace = 274877906944L,
        PinkWig = 549755813888L,
        RoseBuns = 1099511627776L,
        SharkHood = 1649267441664L,
        FrogCap = 2199023255552L,
        PixelCrown = 2748779069440L,
        GoggleEyes = 4398046511104L,
        SwirlGlasses = 8796093022208L,
        CensorBar = 13194139533312L,
        SweatSticker = 17592186044416L,
        MemeMoustache = 21990232555520L,
        BellCollar = 35184372088832L,
        GiantBow = 70368744177664L,
        NoodleScarf = 105553116266496L,
        ChunkyChain = 140737488355328L,
        CameraStrap = 175921860444160L,
        ToastBackpack = 281474976710656L,
        SharkTail = 562949953421312L,
        SpeechSign = 844424930131968L,
        PixelWings = 1125899906842624L,
        CozyBlanket = 1407374883553280L,
        FishPlush = 2251799813685248L,
        SqueakyHammer = 4503599627370496L,
        TeaCup = 6755399441055744L,
        Banana = 9007199254740992L,
        TinyKeyboard = 11258999068426240L,
        BlankFace = 18014398509481984L,
        SmugFace = 36028797018963968L,
        PanicFace = 54043195528445952L,
        SquishFace = 72057594037927936L,
        WideGrinFace = 90071992547409920L,
        LongCatBody = 144115188075855872L,
        PuddingBody = 288230376151711744L,
        MarshmallowBody = 432345564227567616L,
        GhostBody = 576460752303423488L,
        BlockBody = 720575940379279360L
    }

    public enum AvatarPartSlot { Head, Face, Neck, Back, Hand, Expression = 5, Clothing = 6, Body = 7 }

    public sealed class AvatarPartDefinition
    {
        public string Id { get; }
        public string Name { get; }
        public int Price { get; }
        public AvatarAccessory Accessory { get; }
        public AvatarPartSlot Slot { get; }

        public AvatarPartDefinition(string id, string name, int price, AvatarAccessory accessory, AvatarPartSlot slot)
        {
            Id = id; Name = name; Price = price; Accessory = accessory; Slot = slot;
        }
    }

    public static class AvatarParts
    {
        public const long RETIRED_CLOTHING_MASK = 4063232L;
        public const long LEGACY_MASK = ((1L << 39) - 1) & ~RETIRED_CLOTHING_MASK;
        public const long ALL_MASK = ((1L << 60) - 1) & ~RETIRED_CLOTHING_MASK;
        public static IReadOnlyList<AvatarAccessory> DefaultAccessories { get; } = Array.AsReadOnly(new[]
        {
            AvatarAccessory.Beret, AvatarAccessory.Wink, AvatarAccessory.RoundBody
        });
        public static IReadOnlyList<AvatarPartSlot> Slots { get; } = Array.AsReadOnly(new[]
        {
            AvatarPartSlot.Head, AvatarPartSlot.Expression, AvatarPartSlot.Face, AvatarPartSlot.Body,
            AvatarPartSlot.Neck, AvatarPartSlot.Back, AvatarPartSlot.Hand
        });
        public static IReadOnlyList<AvatarPartDefinition> Items { get; } = Array.AsReadOnly(new[]
        {
            new AvatarPartDefinition("beret", "베레모", 100, AvatarAccessory.Beret, AvatarPartSlot.Head),
            new AvatarPartDefinition("brush", "붓", 100, AvatarAccessory.Brush, AvatarPartSlot.Hand),
            new AvatarPartDefinition("crown", "별빛 왕관", 150, AvatarAccessory.Crown, AvatarPartSlot.Head),
            new AvatarPartDefinition("wizard-hat", "마법사 모자", 180, AvatarAccessory.WizardHat, AvatarPartSlot.Head),
            new AvatarPartDefinition("headphones", "민트 헤드폰", 140, AvatarAccessory.Headphones, AvatarPartSlot.Head),
            new AvatarPartDefinition("round-glasses", "동그란 안경", 100, AvatarAccessory.RoundGlasses, AvatarPartSlot.Face),
            new AvatarPartDefinition("sunglasses", "선글라스", 120, AvatarAccessory.Sunglasses, AvatarPartSlot.Face),
            new AvatarPartDefinition("scarf", "포근한 머플러", 120, AvatarAccessory.Scarf, AvatarPartSlot.Neck),
            new AvatarPartDefinition("bow-tie", "리본 나비넥타이", 100, AvatarAccessory.BowTie, AvatarPartSlot.Neck),
            new AvatarPartDefinition("cape", "별빛 망토", 180, AvatarAccessory.Cape, AvatarPartSlot.Back),
            new AvatarPartDefinition("star-wand", "별 마법봉", 160, AvatarAccessory.StarWand, AvatarPartSlot.Hand),
            new AvatarPartDefinition("palette", "물감 팔레트", 140, AvatarAccessory.Palette, AvatarPartSlot.Hand),
            new AvatarPartDefinition("wink", "윙크", 100, AvatarAccessory.Wink, AvatarPartSlot.Expression),
            new AvatarPartDefinition("happy", "활짝 웃음", 100, AvatarAccessory.Happy, AvatarPartSlot.Expression),
            new AvatarPartDefinition("sleepy", "졸린 얼굴", 100, AvatarAccessory.Sleepy, AvatarPartSlot.Expression),
            new AvatarPartDefinition("surprised", "놀란 얼굴", 120, AvatarAccessory.Surprised, AvatarPartSlot.Expression),
            new AvatarPartDefinition("determined", "단호한 얼굴", 140, AvatarAccessory.Determined, AvatarPartSlot.Expression),
            new AvatarPartDefinition("round-body", "동그란 몸통", 180, AvatarAccessory.RoundBody, AvatarPartSlot.Body),
            new AvatarPartDefinition("soft-square-body", "말랑 네모 몸통", 180, AvatarAccessory.SoftSquareBody, AvatarPartSlot.Body),
            new AvatarPartDefinition("droplet-body", "물방울 몸통", 200, AvatarAccessory.DropletBody, AvatarPartSlot.Body),
            new AvatarPartDefinition("bean-body", "콩알 몸통", 180, AvatarAccessory.BeanBody, AvatarPartSlot.Body),
            new AvatarPartDefinition("cat-body", "고양이 몸통", 200, AvatarAccessory.CatBody, AvatarPartSlot.Body),
            new AvatarPartDefinition("bear-body", "곰돌이 몸통", 200, AvatarAccessory.BearBody, AvatarPartSlot.Body),
            new AvatarPartDefinition("blush", "수줍은 얼굴", 100, AvatarAccessory.Blush, AvatarPartSlot.Expression),
            new AvatarPartDefinition("mischievous", "장난스러운 얼굴", 140, AvatarAccessory.Mischievous, AvatarPartSlot.Expression),
            new AvatarPartDefinition("tearful", "울먹이는 얼굴", 120, AvatarAccessory.Tearful, AvatarPartSlot.Expression),
            new AvatarPartDefinition("heart-eyes", "하트 눈", 140, AvatarAccessory.HeartEyes, AvatarPartSlot.Expression),
            new AvatarPartDefinition("star-eyes", "반짝 눈", 140, AvatarAccessory.StarEyes, AvatarPartSlot.Expression),
            new AvatarPartDefinition("spiral-eyes", "빙글 눈", 120, AvatarAccessory.SpiralEyes, AvatarPartSlot.Expression),
            new AvatarPartDefinition("pixel-face", "픽셀 얼굴", 180, AvatarAccessory.PixelFace, AvatarPartSlot.Expression),
            new AvatarPartDefinition("skull-face", "해골 얼굴", 180, AvatarAccessory.SkullFace, AvatarPartSlot.Expression),
            new AvatarPartDefinition("cat-face", "냥냥 얼굴", 160, AvatarAccessory.CatFace, AvatarPartSlot.Expression),
            new AvatarPartDefinition("sparkle-face", "초롱초롱 얼굴", 160, AvatarAccessory.SparkleFace, AvatarPartSlot.Expression),
            new AvatarPartDefinition("mellow-face", "나른한 얼굴", 160, AvatarAccessory.MellowFace, AvatarPartSlot.Expression),
            new AvatarPartDefinition("pink-wig", "분홍 삐죽 가발", 220, AvatarAccessory.PinkWig, AvatarPartSlot.Head),
            new AvatarPartDefinition("rose-buns", "장미 만두머리", 240, AvatarAccessory.RoseBuns, AvatarPartSlot.Head),
            new AvatarPartDefinition("shark-hood", "아기 상어 후드", 220, AvatarAccessory.SharkHood, AvatarPartSlot.Head),
            new AvatarPartDefinition("frog-cap", "왕눈 개구리 모자", 200, AvatarAccessory.FrogCap, AvatarPartSlot.Head),
            new AvatarPartDefinition("pixel-crown", "픽셀 왕관", 200, AvatarAccessory.PixelCrown, AvatarPartSlot.Head),
            new AvatarPartDefinition("goggle-eyes", "왕눈 안경", 140, AvatarAccessory.GoggleEyes, AvatarPartSlot.Face),
            new AvatarPartDefinition("swirl-glasses", "빙글 안경", 160, AvatarAccessory.SwirlGlasses, AvatarPartSlot.Face),
            new AvatarPartDefinition("censor-bar", "시크릿 눈가리개", 140, AvatarAccessory.CensorBar, AvatarPartSlot.Face),
            new AvatarPartDefinition("sweat-sticker", "진땀 스티커", 120, AvatarAccessory.SweatSticker, AvatarPartSlot.Face),
            new AvatarPartDefinition("meme-moustache", "과장 콧수염", 140, AvatarAccessory.MemeMoustache, AvatarPartSlot.Face),
            new AvatarPartDefinition("bell-collar", "방울 목걸이", 140, AvatarAccessory.BellCollar, AvatarPartSlot.Neck),
            new AvatarPartDefinition("giant-bow", "왕리본", 160, AvatarAccessory.GiantBow, AvatarPartSlot.Neck),
            new AvatarPartDefinition("noodle-scarf", "면발 머플러", 160, AvatarAccessory.NoodleScarf, AvatarPartSlot.Neck),
            new AvatarPartDefinition("chunky-chain", "통통 체인", 180, AvatarAccessory.ChunkyChain, AvatarPartSlot.Neck),
            new AvatarPartDefinition("camera-strap", "미니 카메라", 180, AvatarAccessory.CameraStrap, AvatarPartSlot.Neck),
            new AvatarPartDefinition("toast-backpack", "식빵 배낭", 180, AvatarAccessory.ToastBackpack, AvatarPartSlot.Back),
            new AvatarPartDefinition("shark-tail", "상어 꼬리", 200, AvatarAccessory.SharkTail, AvatarPartSlot.Back),
            new AvatarPartDefinition("speech-sign", "말풍선 팻말", 180, AvatarAccessory.SpeechSign, AvatarPartSlot.Back),
            new AvatarPartDefinition("pixel-wings", "픽셀 날개", 200, AvatarAccessory.PixelWings, AvatarPartSlot.Back),
            new AvatarPartDefinition("cozy-blanket", "폭닥 담요", 180, AvatarAccessory.CozyBlanket, AvatarPartSlot.Back),
            new AvatarPartDefinition("fish-plush", "생선 인형", 160, AvatarAccessory.FishPlush, AvatarPartSlot.Hand),
            new AvatarPartDefinition("squeaky-hammer", "삑삑 망치", 160, AvatarAccessory.SqueakyHammer, AvatarPartSlot.Hand),
            new AvatarPartDefinition("tea-cup", "느긋한 찻잔", 160, AvatarAccessory.TeaCup, AvatarPartSlot.Hand),
            new AvatarPartDefinition("banana", "잘 익은 바나나", 140, AvatarAccessory.Banana, AvatarPartSlot.Hand),
            new AvatarPartDefinition("tiny-keyboard", "미니 키보드", 180, AvatarAccessory.TinyKeyboard, AvatarPartSlot.Hand),
            new AvatarPartDefinition("blank-face", "멍한 얼굴", 120, AvatarAccessory.BlankFace, AvatarPartSlot.Expression),
            new AvatarPartDefinition("smug-face", "능글 얼굴", 160, AvatarAccessory.SmugFace, AvatarPartSlot.Expression),
            new AvatarPartDefinition("panic-face", "패닉 얼굴", 160, AvatarAccessory.PanicFace, AvatarPartSlot.Expression),
            new AvatarPartDefinition("squish-face", "찌그러진 얼굴", 160, AvatarAccessory.SquishFace, AvatarPartSlot.Expression),
            new AvatarPartDefinition("wide-grin-face", "씩 웃는 얼굴", 160, AvatarAccessory.WideGrinFace, AvatarPartSlot.Expression),
            new AvatarPartDefinition("long-cat-body", "길쭉 고양이 몸통", 220, AvatarAccessory.LongCatBody, AvatarPartSlot.Body),
            new AvatarPartDefinition("pudding-body", "탱글 푸딩 몸통", 220, AvatarAccessory.PuddingBody, AvatarPartSlot.Body),
            new AvatarPartDefinition("marshmallow-body", "마시멜로 몸통", 220, AvatarAccessory.MarshmallowBody, AvatarPartSlot.Body),
            new AvatarPartDefinition("ghost-body", "말랑 유령 몸통", 240, AvatarAccessory.GhostBody, AvatarPartSlot.Body),
            new AvatarPartDefinition("block-body", "픽셀 블록 몸통", 220, AvatarAccessory.BlockBody, AvatarPartSlot.Body)
        });

        public static ShopProduct[] CreateShopProducts()
        {
            var products = new List<ShopProduct>(Items.Count + 1);
            for (int index = 0; index < Items.Count; index++)
            {
                var part = Items[index];
                products.Add(new ShopProduct { Id = part.Id, Name = part.Name, Price = part.Price, Accessory = (long)part.Accessory });
                if (index == 1)
                    products.Add(new ShopProduct { Id = "painter", Name = "화가 세트", Price = 180, Accessory = (long)AvatarAccessory.Painter });
            }
            return products.ToArray();
        }

        public static long Mask(AvatarPartSlot slot)
        {
            switch (slot)
            {
                case AvatarPartSlot.Head: return 1 | 4 | 8 | 16 | ExtensionMask(slot);
                case AvatarPartSlot.Face: return 32 | 64 | ExtensionMask(slot);
                case AvatarPartSlot.Neck: return 128 | 512 | ExtensionMask(slot);
                case AvatarPartSlot.Back: return 256 | ExtensionMask(slot);
                case AvatarPartSlot.Hand: return 2 | 1024 | 2048 | ExtensionMask(slot);
                case AvatarPartSlot.Expression: return 4096L | 8192L | 16384L | 32768L | 65536L | 268435456L | 536870912L | 1073741824L
                    | 2147483648L | 4294967296L | 8589934592L | 17179869184L | 34359738368L | 68719476736L | 137438953472L | 274877906944L | ExtensionMask(slot);
                case AvatarPartSlot.Clothing: return 0;
                case AvatarPartSlot.Body: return 4194304 | 8388608 | 16777216 | 33554432 | 67108864 | 134217728 | ExtensionMask(slot);
                default: return 0;
            }
        }

        public static string Name(AvatarPartSlot slot)
        {
            switch (slot)
            {
                case AvatarPartSlot.Head: return "머리";
                case AvatarPartSlot.Face: return "얼굴 장식";
                case AvatarPartSlot.Neck: return "목";
                case AvatarPartSlot.Back: return "등";
                case AvatarPartSlot.Hand: return "손";
                case AvatarPartSlot.Expression: return "얼굴";
                case AvatarPartSlot.Body: return "몸통";
                default: return "";
            }
        }

        public static bool IsValid(long equipment)
        {
            if ((equipment & ~ALL_MASK) != 0) return false;
            foreach (var slot in Slots)
            {
                long legacy = equipment & Mask(slot) & LEGACY_MASK;
                long extension = equipment & ExtensionMask(slot);
                if (extension != 0)
                {
                    if (legacy != 0 || (extension >> ExtensionShift(slot)) > 5) return false;
                }
                else if ((legacy & (legacy - 1)) != 0) return false;
            }
            return true;
        }

        public static long Get(long equipment, AvatarPartSlot slot)
        {
            if (equipment < 0) return 0;
            long legacy = equipment & Mask(slot) & LEGACY_MASK;
            if (legacy != 0) return legacy & -legacy;
            long extension = equipment & ExtensionMask(slot);
            return (extension >> ExtensionShift(slot)) <= 5 ? extension : 0;
        }

        public static long Sanitize(long equipment)
        {
            if (equipment < 0) return 0;
            long result = 0;
            foreach (var slot in Slots)
            {
                result |= Get(equipment, slot);
            }
            return result;
        }

        public static long Equip(long equipment, long parts)
        {
            long result = Sanitize(equipment);
            if (!IsValid(parts)) return result;
            foreach (var slot in Slots)
            {
                long mask = Mask(slot);
                if ((parts & mask) != 0) result = (result & ~mask) | (parts & mask);
            }
            return result;
        }

        public static long Remove(long equipment, AvatarPartSlot slot) => Sanitize(equipment) & ~Mask(slot);

        public static bool IsEquipped(long equipment, long parts)
        {
            if (parts == 0 || !IsValid(parts)) return false;
            foreach (var slot in Slots)
            {
                long part = parts & Mask(slot);
                if (part != 0 && Get(equipment, slot) != part) return false;
            }
            return true;
        }

        public static bool IsOwned(IEnumerable<long> owned, long parts)
        {
            if (!IsValid(parts)) return false;
            if (parts == 0) return true;
            if (owned == null) return false;
            long remaining = parts;
            foreach (long item in owned)
            {
                if (item < 0) continue;
                remaining &= ~(item & LEGACY_MASK);
                foreach (var slot in Slots)
                {
                    long extension = parts & ExtensionMask(slot);
                    if (extension != 0 && (item & Mask(slot)) == extension) remaining &= ~extension;
                }
                if (remaining == 0) return true;
            }
            return false;
        }

        public static long KeepOwned(long equipment, IEnumerable<long> owned)
        {
            if (owned == null) return 0;
            var entries = new List<long>(owned);
            long result = 0;
            foreach (var slot in Slots)
            {
                long part = Get(equipment, slot);
                if (IsOwned(entries, part)) result |= part;
            }
            return result;
        }

        private static long ExtensionMask(AvatarPartSlot slot)
        {
            int shift = ExtensionShift(slot);
            return shift == 0 ? 0 : 7L << shift;
        }

        private static int ExtensionShift(AvatarPartSlot slot)
        {
            switch (slot)
            {
                case AvatarPartSlot.Head: return 39;
                case AvatarPartSlot.Face: return 42;
                case AvatarPartSlot.Neck: return 45;
                case AvatarPartSlot.Back: return 48;
                case AvatarPartSlot.Hand: return 51;
                case AvatarPartSlot.Expression: return 54;
                case AvatarPartSlot.Body: return 57;
                default: return 0;
            }
        }
    }
}
