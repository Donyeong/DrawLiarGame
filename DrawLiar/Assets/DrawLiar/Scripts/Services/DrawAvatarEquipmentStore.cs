using System.Globalization;
using UnityEngine;

namespace DrawLiar
{
    public static class DrawAvatarEquipmentStore
    {
        private const string EQUIPMENT_KEY = "DrawLiar.Equipment64";
        private const string LEGACY_EQUIPMENT_KEY = "DrawLiar.Equipment";

        public static long Load()
        {
            if (PlayerPrefs.HasKey(EQUIPMENT_KEY)
                && long.TryParse(PlayerPrefs.GetString(EQUIPMENT_KEY), NumberStyles.None, CultureInfo.InvariantCulture, out long equipment))
                return AvatarParts.Sanitize(equipment);
            int legacy = PlayerPrefs.GetInt(LEGACY_EQUIPMENT_KEY, (int)AvatarAccessory.Painter);
            return AvatarParts.Sanitize(legacy < 0 ? 0L : legacy);
        }

        public static void Save(long equipment)
        {
            equipment = AvatarParts.Sanitize(equipment);
            PlayerPrefs.SetString(EQUIPMENT_KEY, equipment.ToString(CultureInfo.InvariantCulture));
            PlayerPrefs.SetInt(LEGACY_EQUIPMENT_KEY, (int)(equipment & int.MaxValue));
        }
    }
}
