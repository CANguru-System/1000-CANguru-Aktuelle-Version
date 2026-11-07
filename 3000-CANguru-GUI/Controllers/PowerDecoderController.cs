using System;

namespace CANguru.Controllers
{
    // Stromanzeige auf der Hauptseite: Ströme aus dem Alive des Stromdecoders,
    // Abschaltgrenze aus seinem Parameter 0 (für die Farbe der Anzeige)
    public class PowerDecoderController
    {
        public int Current0_mA { get; private set; }
        public int Current1_mA { get; private set; }
        public int Threshold_mA { get; set; } = 1000;

        public event Action ValuesUpdated;
        public event Action<int> ShutdownTriggered;

        public void UpdateCurrent(int channel, int mA)
        {
            if (channel == 0)
                Current0_mA = mA;
            else
                Current1_mA = mA;

            ValuesUpdated?.Invoke();
        }

        public void TriggerShutdown(int channel)
        {
            ShutdownTriggered?.Invoke(channel);
        }
    }
}
