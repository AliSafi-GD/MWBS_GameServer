using System;
using UnityEngine;

namespace Game.Core
{
    /// <summary>Choices the player makes in Settings. Kept on the device.</summary>
    public static class GameSettings
    {
        private const string AutoAimKey = "game.autoAim";
        private const string PingKey = "game.showPing";

        public static event Action Changed;

        /// <summary>
        /// On: the gun turns to the enemy by itself and the fire button only shoots.
        /// Off: you turn the gun yourself by dragging on the fire button while you shoot.
        /// </summary>
        public static bool AutoAim
        {
            get => PlayerPrefs.GetInt(AutoAimKey, 1) == 1;
            set => Set(AutoAimKey, value);
        }

        /// <summary>Show the connection delay (ping) during a match.</summary>
        public static bool ShowPing
        {
            get => PlayerPrefs.GetInt(PingKey, 1) == 1;
            set => Set(PingKey, value);
        }

        private static void Set(string key, bool value)
        {
            PlayerPrefs.SetInt(key, value ? 1 : 0);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }
    }
}
