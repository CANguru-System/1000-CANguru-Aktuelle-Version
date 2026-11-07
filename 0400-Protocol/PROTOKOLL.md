# CANguru-Protokoll Version 2

Gilt für die Verbindungen **GUI ↔ Bridge** (UDP, Bridge-Port 5000, GUI-Port 5001) und
**Bridge ↔ Decoder** (ESP-NOW). Nicht betroffen sind das Märklin-CAN-Format zur Gleisbox und das
UDP-Format zu WinDigiPet. Diese Märklin-Frames werden im Feature CAN unverändert getunnelt.

| Datei | Inhalt |
|---|---|
| `0300-Constants/constants_manager.py` | alle Zahlenwerte (Präfix `CG2_`), daraus `protocol_constants_core.h` und `ProtocolConstants.cs` |
| `0400-Protocol/cpp/canguru_protocol.h` | Bibliothek für Decoder und Bridge (Kopf zerlegen/bauen, Lesen/Schreiben, Duplikate, CRC-32) |
| `0400-Protocol/cs/CanguruProtocol.cs` | dieselbe Bibliothek für die GUI |
| `0400-Protocol/test/` | Testvektoren und Tests für beide Sprachen, laufen in `alles-bauen.ps1` |

Bibliothek und Konstanten werden von `alles-bauen.ps1` in die Projekte kopiert. **Die Kopien nicht von Hand ändern.**

---

## 1. Nachrichtenkopf

| Byte | Feld | Inhalt |
|---|---|---|
| 0 | Kennung | `CG2_MAGIC` = `0xC2` (CANguru, Hauptversion 2) |
| 1 | Feature | siehe Abschnitt 5 |
| 2 | Kommando | innerhalb des Features |
| 3 | Flags | siehe 1.1 |
| 4 | Folgenummer | 0–255, je Absender fortlaufend |
| 5 | Decoder | Ziel (zur Bridge/zum Decoder) bzw. Absender (zur GUI/von einem Decoder), siehe 2. |
| 6–7 | Länge | Anzahl Byte Nutzdaten, Little Endian |
| 8 … | Nutzdaten | |

Alle Mehrbyte-Zahlen in Kopf und Nutzdaten sind **Little Endian**. Ausgenommen sind nur die
Übergangs-Features Stepper und Rückmelder (5.11, 5.12), die die bisherigen Nutzdaten unverändert übernehmen.

Texte haben die Form `[Länge][UTF-8-Bytes]`, höchstens 255 Byte.

### 1.1 Flags
| Bit | Konstante | Bedeutung |
|---|---|---|
| 0 | `CG2_FLAG_ACK_REQ` | Der Absender erbittet eine Quittung. |
| 1 | `CG2_FLAG_RESPONSE` | Diese Nachricht ist Antwort/Quittung auf die Anfrage mit derselben Folgenummer. |
| 2 | `CG2_FLAG_ERROR` | zusammen mit Bit 1: Die Anfrage ist fehlgeschlagen, Nutzdaten[0] = Fehlercode (1.2) |
| 4–5 | `CG2_ADDR_*` | Adressierungsart: 0 = ein Decoder (`CG2_ADDR_ID`), 1 = alle eines Typs (`CG2_ADDR_TYPE`), 2 = alle (`CG2_ADDR_ALL`) |

### 1.2 Fehlercodes
| Wert | Konstante | Bedeutung |
|---|---|---|
| 1 | `CG2_ERR_UNKNOWN_FEATURE` | Feature unbekannt |
| 2 | `CG2_ERR_UNKNOWN_COMMAND` | Kommando unbekannt |
| 3 | `CG2_ERR_BAD_LENGTH` | Nutzdaten zu kurz |
| 4 | `CG2_ERR_BAD_VALUE` | Wert außerhalb des zulässigen Bereichs |
| 5 | `CG2_ERR_BUSY` | gerade nicht möglich (z. B. Kalibrierung oder OTA läuft) |
| 6 | `CG2_ERR_NOT_DELIVERED` | nur von der Bridge: Der Decoder hat trotz Wiederholung nicht geantwortet. |
| 7 | `CG2_ERR_UNKNOWN_DECODER` | nur von der Bridge: Zu dieser ID ist kein Decoder bekannt. |

---

## 2. Adressierung

**Byte 5 (Decoder):**
- **GUI → Bridge:** Ziel-Decoder-ID. `0` (`CG2_ID_BRIDGE`) heißt, die Nachricht ist für die Bridge selbst.
- **Bridge → GUI:** Absender-ID. `0` heißt, die Nachricht stammt von der Bridge.
- **Bridge → Decoder:** je nach Adressierungsart die Decoder-ID, der Decodertyp (`DEVTYPE_*`) oder unbedeutend (alle).
- **Decoder → Bridge:** die eigene ID. Vor der ID-Vergabe steht hier `INVALID_ASSIGNED_ID` (0). Die Bridge erkennt den Decoder dann an der MAC.

**Übertragung über ESP-NOW:**
- Adressierungsart **ID:** Unicast an die MAC des Decoders. ESP-NOW bestätigt und wiederholt selbst.
- Adressierungsarten **TYPE** und **ALL:** Broadcast. Jeder Decoder prüft selbst, ob er gemeint ist.

## 3. Zustellung

1. Wer eine Quittung braucht, setzt `CG2_FLAG_ACK_REQ`.
2. Der Empfänger antwortet mit `CG2_FLAG_RESPONSE`, gleichem Feature, Kommando und gleicher Folgenummer (`cg2ResponseTo`). Bei einem Fehler kommt zusätzlich `CG2_FLAG_ERROR` mit dem Fehlercode dazu.
3. Ohne Antwort wiederholt der Absender bis zu `CG2_RETRY_COUNT` (3) Mal im Abstand von `CG2_RETRY_MS` (150 ms). Erhält die Bridge von einem Decoder keine Antwort, meldet sie der GUI `CG2_ERR_NOT_DELIVERED`.
4. Kommt eine Anfrage mit derselben Folgenummer erneut (`Cg2DuplicateFilter`), wird sie **nicht noch einmal ausgeführt**, sondern nur erneut quittiert.

**Mit Quittung:** Parameter schreiben, Aktionen, Reset, Werkseinstellungen, ID-Vergabe, OTA-Start, Datei- und OTA-Blöcke.
**Ohne Quittung:** Alive, Zusammenfassung, CAN-Tunnel, Ereignisse, Fortschritt.

## 4. Kompatibilität

- Unbekannte Features und Kommandos werden **ignoriert**. Bei erbetener Quittung antwortet der Empfänger mit `CG2_ERR_UNKNOWN_FEATURE` bzw. `_COMMAND`.
- Nutzdaten dürfen in neuen Versionen **nur hinten verlängert** werden. Empfänger lesen, was sie kennen, und ignorieren den Rest.
- `CG2_MAGIC` ändert sich nur bei einem echten Bruch. Kleinere Erweiterungen zählen `CG2_PROTO_MINOR` hoch, mitgeteilt in `HELLO` und `DISCOVER_REPLY`.
- **Altes Format:** Nachrichten, deren erstes Byte nicht `0xC2` ist, stammen von Firmware vor Version 2 (`cg2IsV2`). Sie werden ignoriert (die Bridge meldet das seriell); solche Decoder müssen einmal per USB aktualisiert werden.

---

## 5. Features

Richtung: G = GUI, B = Bridge, D = Decoder. „→ Antwort“ beschreibt die Nutzdaten der Antwort.

### 5.1 Sitzung `CG2_F_SESSION` (0x01)
| Kommando | Richtung | Nutzdaten |
|---|---|---|
| `DISCOVER` 0x01 | G → B (Broadcast) | `[protoMinor]` |
| `DISCOVER_REPLY` 0x02 | B → G | `[fwMajor][fwMinor][protoMinor][gleisbox 0/1]` |
| `START` 0x03 | G → B, B → D (ALL) | – . Die Bridge fordert alle Decoder zur Anmeldung auf: beim Start der Sitzung und danach alle 5 s (außer während OTA), damit sich auch später eingeschaltete Decoder anmelden. Jeder Decoder antwortet mit HELLO; ein angemeldeter, unveränderter Decoder erhält seine ID erneut, die GUI bekommt kein weiteres `ADDED`. |
| `END` 0x04 | G → B | – . Die GUI wird beendet: Gleisbox STOPP, Decoder-Reset, Neustart der Bridge. |
| `TRACK_POWER` 0x05 | B → D (ALL), B → D (ID), B → G | `[an 0/1]`. Gleisspannung, abgeleitet aus STOPP/GO am CAN-Bus, an **alle** Decoder (z. B. LED-Signale bei STOPP auf Rot) und an die GUI (Anzeige STOPP/GO). Ein neu angemeldeter Decoder erhält den aktuellen Stand sofort per Unicast. |

### 5.2 Decoder `CG2_F_DECODER` (0x02)
| Kommando | Richtung | Nutzdaten |
|---|---|---|
| `HELLO` 0x01 | D → B | `[typ][mac 6][fwMajor][fwMinor][protoMinor]` |
| `ASSIGN_ID` 0x02 | B → D (Unicast) | `[mac 6][id]`. Der Decoder prüft die MAC und quittiert. |
| `ADDED` 0x03 | B → G | `[id][typ][mac 6][fwMajor][fwMinor][protoMinor][reserviert 0]` |
| `REJOIN` 0x04 | D → B | `[fehlergrund]` (Absender-ID in Byte 5). Grund nach einem OTA-Neustart: `CG2_OTA_FAIL_NONE` 0 ok, `_WIFI` 1 kein WLAN der Bridge, `_HTTP` 2 Datei nicht abrufbar, `_SIZE` 3 Datei zu groß, `_DOWNLOAD` 4 Download unvollständig, `_INVALID` 5 keine gültige Firmware oder Firmware für einen anderen Chip (der Decoder prüft die Chip-Kennung im Kopf der Datei schon beim ersten Block) |
| `REMOVE` 0x05 | G → B | `[id]`. Den Eintrag aus der dauerhaften Decoder-Liste löschen. |
| `RESET` 0x06 | B → D | – . Der Decoder startet neu und meldet sich neu an. |

### 5.3 Alive `CG2_F_ALIVE` (0x03)
| Kommando | Richtung | Nutzdaten |
|---|---|---|
| `ALIVE` 0x01 | D → B → G | `[status][anzahl][wert u16] × anzahl`. Status: 0 Fehler, 1 OK, 2 Warnung. Die Werte sind die Messwerte des Decoders in der Reihenfolge seiner Beschreibung (5.4, Art 2); `0xFFFF` = nicht verfügbar (z. B. Kanal nicht angeschlossen). |
| `SUMMARY` 0x02 | B → G | `[anzahl][typ][zahl] × anzahl`: wie viele Decoder je Typ bereit sind |

### 5.4 Parameter `CG2_F_PARAM` (0x04)
| Kommando | Richtung | Nutzdaten |
|---|---|---|
| `DESCRIBE_GET` 0x01 | G → D | `[art][nummer]`, Art: 0 Parameter, 1 Aktion, 2 Messwert |
| `DESCRIBE` 0x02 | D → G (Antwort) | siehe unten |
| `READ` 0x03 | G → D | `[nummer]` → Antwort `[nummer][wert u16]` |
| `WRITE` 0x04 | G → D | `[nummer][wert u16]` → Antwort `[nummer][wert u16]`, also den tatsächlich gespeicherten Wert |
| `FACTORY_RESET` 0x05 | G → D | – . Alle Parameter auf den Standardwert, die Zubehöradresse bleibt erhalten. |

Antwort auf `DESCRIBE_GET` (ein Eintrag je Anfrage, passt sicher in ein ESP-NOW-Paket):
```
[art][nummer][anzahl gesamt][datentyp]
[min u16][max u16][standard u16][schritt u16]
[einheit text][name text][anzahlAuswahl][auswahl text] × anzahlAuswahl
```
Datentyp: 0 u8, 1 u16, 2 Auswahl (Wert = Index), 3 Ja/Nein.
Bei Aktionen und Messwerten bleiben min/max/standard/schritt 0, sofern sie keine Bedeutung haben.
Mit `nummer` ≥ `anzahl gesamt` antwortet der Decoder mit `CG2_ERR_BAD_VALUE`. Ein Decoder ohne Einträge einer Art
antwortet also schon auf Nummer 0 mit diesem Fehler.

Beim `WRITE` begrenzt der Decoder den Wert: u8/u16 auf min…max, gerundet auf die Schrittweite (gezählt ab min);
Ja/Nein wird zu 0 oder 1. Ein Auswahlindex außerhalb der Liste ergibt `CG2_ERR_BAD_VALUE`. Die Antwort enthält
den tatsächlich gespeicherten Wert. Die Bearbeitung übernimmt der Decoder-Kern (`Core_Param.cpp`) anhand der
Tabellen des Decodertyps (`DecoderBase::paramDef`, `actionDef`, `valueDef`).

**Parameter des Kerns** folgen bei jedem Decoder auf die Parameter des Typs (Nummern ab der Anzahl der Typ-Parameter;
`anzahl gesamt` zählt sie mit). Bisher einer: **Sendeleistung** (Auswahl 8,5 / 11 / 15 / 19,5 dBm), gilt für ESP-NOW und
das OTA-WLAN. Standard 8,5 dBm bei ESP32-C3 (viele SuperMini-Module verbinden sich bei voller Leistung schlecht),
19,5 dBm beim klassischen ESP32. Bleibt bei Werkseinstellungen erhalten.

### 5.5 Aktion `CG2_F_ACTION` (0x05)
| Kommando | Richtung | Nutzdaten |
|---|---|---|
| `EXECUTE` 0x01 | G → D | `[aktion][argumente …]`. Die Bedeutung steht in der Beschreibung (Art 1). |

### 5.6 Ereignis `CG2_F_EVENT` (0x06)
| Kommando | Richtung | Nutzdaten |
|---|---|---|
| `EVENT` 0x01 | D → B → G | `[ereignis][daten …]` |

| Ereignis | Daten |
|---|---|
| `CG2_EV_POSITION` 0x01 | `[kanal][stellung]`. Stellung 0 = rot / abzweigend, 1 = grün / gerade. Gesendet, wenn die Stellung **erreicht** ist; erst dann geht auch die Rückmeldung `ACC_R` an WDP. |
| `CG2_EV_OVERCURRENT` 0x02 | `[kanal][strom mA u16]`. Die Bridge sendet zusätzlich STOPP an die Gleisbox. |
| `CG2_EV_CALIBRATED` 0x03 | `[kanal][laufweg u16]` |

Zubehör-Decoder (Weiche `0x52`, Formsignal `0x51`, LED-Signal `0x58` mit acht Kanälen am PCA9685) melden im Alive je Kanal die Stellung (0/1, 2 = unterwegs oder unbekannt).
Märklin-Stellbefehle (`SWITCH_ACC`) gibt die Bridge an **alle** Decoder weiter; nur Zubehör-Decoder werten sie aus.
Der Status ist „Warnung“, solange eine Stellung nach einem Stromausfall während der Fahrt unsicher ist.

### 5.7 CAN-Tunnel `CG2_F_CAN` (0x07)
Ein CAN-Frame ist immer das 13-Byte-Märklin-Format (ID 4 Byte, DLC, 8 Datenbytes).

| Kommando | Richtung | Nutzdaten |
|---|---|---|
| `TO_TRACKBOX` 0x01 | G → B | `[frame 13]`. Die Bridge sendet ihn auf den CAN-Bus. |
| `TO_GUI` 0x02 | B → G | `[folge u32][frame 13][quelle]`. Quelle: `M` Gleisbox, `W` WDP, `D` Decoder |
| `TO_DECODERS` 0x03 | B → D (TYPE/ALL) | `[frame 13]` |
| `FROM_DECODER` 0x04 | D → B | `[frame 13]`. Die Bridge leitet an WDP und GUI weiter. |

### 5.8 Datei `CG2_F_FILE` (0x08) – `lokomotive.cs2`
| Kommando | Richtung | Nutzdaten |
|---|---|---|
| `BLOCK` 0x01 | G → B | `[offset u32][daten]` → Antwort `[offset u32]` |
| `END` 0x02 | G → B | `[größe u32][crc32 u32]` → Antwort `[ok 0/1]` |

### 5.9 OTA `CG2_F_OTA` (0x09)
| Kommando | Richtung | Nutzdaten |
|---|---|---|
| `UPLOAD_BEGIN` 0x01 | G → B | `[größe u32][crc32 u32]` |
| `UPLOAD_BLOCK` 0x02 | G → B | `[offset u32][daten bis CG2_OTA_BLOCK_SIZE]` → Antwort `[offset u32]` |
| `START` 0x03 | G → B | `[modus][id oder typ]`, Modus: 0 ein Decoder, 1 alle eines Typs |
| `PROGRESS` 0x04 | B → G | `[id][prozent][gesamtProzent]` |
| `RESULT` 0x05 | B → G | `[id][ergebnis][fehlergrund]`, Ergebnis: 0 ok, 1 fehlgeschlagen, 2 keine Rückmeldung; Fehlergrund wie bei `REJOIN`. Bei den Gründen 1, 2 und 4 versucht die Bridge es nach 3 s ein zweites Mal, bevor sie `RESULT` sendet. Nach dem letzten Decoder sendet sie `PROGRESS` mit Gesamt 100 %. |
| `DECODER_START` 0x06 | B → D (Unicast) | – . Der Decoder verbindet sich mit dem WLAN der Bridge und lädt die Firmware per HTTP. |

### 5.10 Strom `CG2_F_POWER` (0x0A) – entfallen mit Schritt 3.4

Wird nicht mehr verwendet; die Konstanten verschwinden mit Schritt 3.6. Der Stromdecoder (ab Firmware 2.0) nutzt nur
noch allgemeine Features:

| früher | jetzt |
|---|---|
| `SET_THRESHOLD` / `GET_THRESHOLD` | Parameter 0 „Abschaltgrenze“ (mA) |
| `SHUTDOWN [kanal][mA]` | Ereignis `CG2_EV_OVERCURRENT [kanal][mA]`; die Bridge sendet daraufhin STOPP an die Gleisbox |
| `TRACK [an]` | Sitzung `TRACK_POWER [an]` (an alle Decoder und die GUI) |
| Strom je Kanal | Messwerte im Alive laut Beschreibung („Strom Kanal 1/2“, mA, `0xFFFF` = nicht angeschlossen) |

### 5.11 Stepper `CG2_F_STEPPER` (0x0B) – Übergang bis Phase 3
Weichen- und Formsignaldecoder. **Die Kommandos sind die bisherigen Werte `SWITCH_CMD_*`**
(`GET_SETTINGS` 0x31 … `GET_VERSION` 0x3B), auch für Signaldecoder. **Die Nutzdaten bleiben unverändert**,
einschließlich Big Endian und der ID in Nutzdaten[0]. So ändert sich in Phase 2 am Stepper-Code nur der Rahmen.

### 5.12 Rückmelder `CG2_F_FEEDBACK` (0x0C) – entfallen mit Schritt 3.5

Wird nicht mehr verwendet; die Konstanten verschwinden mit Schritt 3.6. Der Rückmelder (ab Firmware 2.0) hat die
Parameter 0 … 15 „Kontakt k: Meldeadresse“ (u8, 1–255, Standard k, bleiben bei Werkseinstellungen erhalten) im
Namensraum „feedback“, Schlüssel `K1` … `K16` (bis Firmware 1.x im Namensraum „decoder“; beim ersten Start einmalig
übernommen). Messwert: belegte Kontakte als Bitmaske (Kontakt 1 = Bit 0).
