using System;
using System.Collections.Generic;

namespace DrawLiar
{
    [Flags]
    public enum AvatarAccessory
    {
        None = 0, Beret = 1, Brush = 2, Crown = 4, WizardHat = 8, Headphones = 16,
        RoundGlasses = 32, Sunglasses = 64, Scarf = 128, Cape = 256, BowTie = 512,
        StarWand = 1024, Palette = 2048, Painter = Beret | Brush,
        Wink = 4096, Happy = 8192, Sleepy = 16384, Surprised = 32768, Determined = 65536,
        PainterApron = 131072, StripedShirt = 262144, PolkaDotShirt = 524288, Overalls = 1048576, StarSweater = 2097152
    }

    public enum AvatarPartSlot { Head, Face, Neck, Back, Hand, Expression = 5, Body = 6 }

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
        public const int ALL_MASK = 4194303;
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
            new AvatarPartDefinition("sleepy", "졸린 표정", 100, AvatarAccessory.Sleepy, AvatarPartSlot.Expression),
            new AvatarPartDefinition("surprised", "놀란 표정", 120, AvatarAccessory.Surprised, AvatarPartSlot.Expression),
            new AvatarPartDefinition("determined", "단호한 표정", 140, AvatarAccessory.Determined, AvatarPartSlot.Expression),
            new AvatarPartDefinition("painter-apron", "화가 앞치마", 140, AvatarAccessory.PainterApron, AvatarPartSlot.Body),
            new AvatarPartDefinition("striped-shirt", "줄무늬 셔츠", 160, AvatarAccessory.StripedShirt, AvatarPartSlot.Body),
            new AvatarPartDefinition("polka-dot-shirt", "물방울 셔츠", 160, AvatarAccessory.PolkaDotShirt, AvatarPartSlot.Body),
            new AvatarPartDefinition("overalls", "멜빵 바지", 180, AvatarAccessory.Overalls, AvatarPartSlot.Body),
            new AvatarPartDefinition("star-sweater", "별 스웨터", 200, AvatarAccessory.StarSweater, AvatarPartSlot.Body)
        });

        public static ShopProduct[] CreateShopProducts()
        {
            var products = new List<ShopProduct>(Items.Count + 1);
            for (int index = 0; index < Items.Count; index++)
            {
                var part = Items[index];
                products.Add(new ShopProduct { Id = part.Id, Name = part.Name, Price = part.Price, Accessory = (int)part.Accessory });
                if (index == 1)
                    products.Add(new ShopProduct { Id = "painter", Name = "화가 세트", Price = 180, Accessory = (int)AvatarAccessory.Painter });
            }
            return products.ToArray();
        }

        public static int Mask(AvatarPartSlot slot)
        {
            switch (slot)
            {
                case AvatarPartSlot.Head: return 1 | 4 | 8 | 16;
                case AvatarPartSlot.Face: return 32 | 64;
                case AvatarPartSlot.Neck: return 128 | 512;
                case AvatarPartSlot.Back: return 256;
                case AvatarPartSlot.Hand: return 2 | 1024 | 2048;
                case AvatarPartSlot.Expression: return 4096 | 8192 | 16384 | 32768 | 65536;
                case AvatarPartSlot.Body: return 131072 | 262144 | 524288 | 1048576 | 2097152;
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
                case AvatarPartSlot.Expression: return "표정";
                case AvatarPartSlot.Body: return "몸통";
                default: return "";
            }
        }

        public static bool IsValid(int equipment)
        {
            if ((equipment & ~ALL_MASK) != 0) return false;
            foreach (var slot in Slots)
            {
                int part = equipment & Mask(slot);
                if ((part & (part - 1)) != 0) return false;
            }
            return true;
        }

        public static int Sanitize(int equipment)
        {
            if (equipment < 0) return 0;
            int result = 0;
            foreach (var slot in Slots)
            {
                int part = equipment & Mask(slot);
                result |= part & -part;
            }
            return result;
        }

        public static int Equip(int equipment, int parts)
        {
            int result = Sanitize(equipment);
            if (!IsValid(parts)) return result;
            foreach (var slot in Slots)
            {
                int mask = Mask(slot);
                if ((parts & mask) != 0) result = (result & ~mask) | (parts & mask);
            }
            return result;
        }

        public static int Remove(int equipment, AvatarPartSlot slot) => Sanitize(equipment) & ~Mask(slot);
        public static bool IsEquipped(int equipment, int parts) => parts != 0 && (equipment & parts) == parts;
    }
}
