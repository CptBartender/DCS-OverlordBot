﻿using System;
using Microsoft.Win32;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.UI
{
    public enum SettingType
    {
        RadioEffects = 0,
        Radio1Channel = 1,
        Radio2Channel = 2,
        Radio3Channel = 3,
        Radio4Channel = 12,
        Radio5Channel = 13,
        Radio6Channel = 14,
        Radio7Channel = 15,
        Radio8Channel = 16,
        Radio9Channel = 17,
        Radio10Channel = 18,
        RadioSwitchIsPTT = 4,
        IntercomChannel = 5,
        RadioClickEffects = 6, // Recieving Radio Effects 
        RadioClickEffectsTx = 7, //Transmitting Radio Effects
        RadioEncryptionEffects = 8, //Radio Encryption effects
        ResampleOutput = 9, //not used - on always
        AutoConnectPrompt = 10, //message about auto connect
        RadioOverlayTaskbarHide = 11,
        RefocusDCS = 19,
        ExpandControls = 20
    }


    public class Settings
    {
        private static Settings _instance;

        public Settings()
        {
            UserSettings = new string[Enum.GetValues(typeof(SettingType)).Length];

            foreach (SettingType set in Enum.GetValues(typeof(SettingType)))
            {
                UserSettings[(int) set] = ReadSetting(set);
            }
        }

        public string[] UserSettings { get; }

        public static Settings Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new Settings();
                }
                return _instance;
            }
        }


        public string ReadSetting(SettingType settingType)
        {
            try
            {
                var setting = (string) Registry.GetValue(InputConfiguration.RegPath,
                    settingType + "_setting",
                    "");
                return setting;
            }
            catch (Exception ex)
            {
            }
            return null;
        }

        public void WriteSetting(SettingType settingType, string setting)
        {
            try
            {
                Registry.SetValue(InputConfiguration.RegPath,
                    settingType + "_setting",
                    setting);

                UserSettings[(int) settingType] = setting;
            }
            catch (Exception ex)
            {
            }
        }
    }
}