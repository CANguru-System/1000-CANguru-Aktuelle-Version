using System;
using System.Collections.Generic;
using System.Text;

namespace CANguru.Protocol
{
    public class AliveManager
    {
        // bereite Decoder je Typ: Strom, Weichen, Formsignale, Rückmelder, LED-Signale
        public event Action<int, int, int, int, int>? AliveSummaryReceived;

        public void ProcessAliveSummary(int power, int sw, int signal, int feedback, int ledSignal)
        {
            AliveSummaryReceived?.Invoke(power, sw, signal, feedback, ledSignal);
        }
    }
}
