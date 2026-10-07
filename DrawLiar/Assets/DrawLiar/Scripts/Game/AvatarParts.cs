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
        PixelFace = 17179869184L, SkullFace = 34359738368L, CatFace = 68719476736L
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
        public const long ALL_MASK = ((1L << 37) - 1) & ~RETIRED_CLOTHING_MASK;
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
            new AvatarPartDefinition("cat-face", "냥냥 얼굴", 160, AvatarAccessory.CatFace, AvatarPartSlot.Expression)
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
                case AvatarPartSlot.Head: return 1 | 4 | 8 | 16;
                case AvatarPartSlot.Face: return 32 | 64;
                case AvatarPartSlot.Neck: return 128 | 512;
                case AvatarPartSlot.Back: return 256;
                case AvatarPartSlot.Hand: return 2 | 1024 | 2048;
                case AvatarPartSlot.Expression: return 4096L | 8192L | 16384L | 32768L | 65536L | 268435456L | 536870912L | 1073741824L
                    | 2147483648L | 4294967296L | 8589934592L | 17179869184L | 34359738368L | 68719476736L;
                case AvatarPartSlot.Clothing: return 0;
                case AvatarPartSlot.Body: return 4194304 | 8388608 | 16777216 | 33554432 | 67108864 | 134217728;
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
                long part = equipment & Mask(slot);
                if ((part & (part - 1)) != 0) return false;
            }
            return true;
        }

        public static long Sanitize(long equipment)
        {
            if (equipment < 0) return 0;
            long result = 0;
            foreach (var slot in Slots)
            {
                long part = equipment & Mask(slot);
                result |= part & -part;
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
        public static bool IsEquipped(long equipment, long parts) => parts != 0 && (equipment & parts) == parts;
    }
}
