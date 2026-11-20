# CANguru-Sniffer

A small Windows tool that listens to the Märklin CAN bus through the CANguru-Bridge,
shows every frame with a plain-language interpretation and saves the recording as a
text file. It was written to study the mfx registration of a Mobile Station 2.

## How it works

The sniffer registers with the bridge the same way the CANguru-GUI does (protocol v2,
`SESSION DISCOVER`). From then on the bridge forwards every frame it receives from the
CAN bus. While recording, the sniffer is a pure listener:

- it puts nothing on the CAN bus,
- it sends no `SESSION START`, so the CANguru decoders are not started.

When it is closed, it sends `SESSION END` like the GUI: the bridge stops the track box,
resets the decoders and restarts, so the next program finds a clean state.

No bridge firmware change is needed.

**Limitation:** the bridge does not receive the frames it transmits itself. Commands from
Win-Digipet or the GUI therefore do not appear – only what the other devices on the bus
say (track box, Mobile Station, ...). For studying the MS2 this is exactly what is wanted.

## Use

1. Close the CANguru-GUI and Win-Digipet. The sniffer receives on the same UDP port as
   the GUI (5001); the GUI would also react to the mfx registration itself. Win-Digipet
   does not cope without the GUI: closing the GUI switches the track power off, and
   Win-Digipet cannot switch it on again.
2. Switch on track box, Mobile Station and bridge.
3. Start `CANguru-Sniffer.exe` and click **Verbinden**.
4. Do what you want to observe, e.g. put a locomotive on the programming track. Use
   **Markierung …** to insert a note into the recording at the right moment.
5. **Speichern …** writes all frames (including hidden pings) as tab-separated text.

Colours: mfx discovery/bind/verify light blue, configuration reads/writes light green,
answers in grey. Yellow lines are markers or lost frames (gap in the bridge's sequence
numbers).

## Build

```
dotnet build -c Release
```

Protocol constants and the protocol library are linked from `0300-Constants` and
`0400-Protocol`, not copied.
