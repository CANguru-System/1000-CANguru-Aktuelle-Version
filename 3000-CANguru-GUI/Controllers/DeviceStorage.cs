using CANguru.Models;
using CANguru.Networking;
using Microsoft.VisualBasic.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace CANguru.Controllers
{
    public class DeviceStorage<T> where T : CANguruDevice
    {

        protected readonly List<T> _devices = new();

        public IReadOnlyList<T> Devices => _devices;

        public virtual void AddOrUpdate(T device)
        {
            var existing = _devices.FirstOrDefault(d => d.AssignedId == device.AssignedId);

            if (existing != null)
            {
                // Gemeinsame Felder aktualisieren
                existing.Name = device.Name;
                existing.LastSeen = device.LastSeen;
                existing.IsReady = device.IsReady;
                existing.ProtocolVersion = device.ProtocolVersion;

                // MAC aktualisieren (falls sich etwas ändert)
                for (int i = 0; i < 6; i++)
                    existing.Mac[i] = device.Mac[i];

                // Spezifische Felder aktualisieren
                CopySpecific(existing, device);
            }
            else
            {
                _devices.Add(device);
            }
        }

        // Kann von abgeleiteten Klassen überschrieben werden
        protected virtual void CopySpecific(T target, T source)
        {
            // Default: nichts tun
        }

        public void Remove(int id)
        {
            var d = _devices.FirstOrDefault(x => x.AssignedId == id);
            if (d != null)
                _devices.Remove(d);
        }

        public void Clear() => _devices.Clear();
    }

}
