import os
import yaml

# ------------------------------------------------------------
# Zentrale Konstanten-Definition
# ------------------------------------------------------------

CONSTANTS = [

# ============================================================
# constants
# ============================================================
dict(name="INVALID_ASSIGNED_ID", value="0x00", arch="core"),


# ============================================================
# Stepper-Zubehör (Weiche, Formsignal): Bereiche der Parameter
# ============================================================
dict(name="step_delay_min",          value="100", arch="core"),
dict(name="step_delay_std",          value="300", arch="core"),
dict(name="step_delay_max",          value="1000", arch="core"),
dict(name="step_delay_step",         value="50", arch="core"),
dict(name="stepsToEnd_min",          value="100", arch="core"),
dict(name="stepsToEnd_std",          value="1140", arch="core"),
dict(name="stepsToEnd_max",          value="1300", arch="core"),
dict(name="stepsToEnd_step",         value="10", arch="core"),


# ============================================================
# BLOCK SIZES & PORTS
# ============================================================
dict(name="BlockSizeGUI",      value="1024",     arch="core"),
dict(name="BlockSizeBridge",   value="BlockSizeGUI+100", arch="core"),
dict(name="BlockSizeDecoder",  value="BlockSizeGUI", arch="core"),
dict(name="BRIDGE_AP_CHANNEL", value="1",      arch="core"),
dict(name="BRIDGE_PORT",       value="5000",   arch="core"),
dict(name="GUI_PORT",          value="5001",   arch="core"),
dict(name="WDP_PORT_OUT",      value="15730",  arch="core"),
dict(name="WDP_PORT_IN",       value="15731",  arch="core"),
dict(name="bdrMonitor",        value="115200", arch="core"),
dict(name="wait_time_small",    value="100",   arch="core"),


# ============================================================
# CAN FRAME CONSTANTS
# ============================================================
dict(name="CAN_FRAME_SIZE", value="13", arch="core"),

dict(name="CANcmd",   value="0x01", arch="core"),
dict(name="hash0",     value="0x02", arch="core"),
dict(name="hash1",     value="0x03", arch="core"),
dict(name="Framelng",  value="0x04", arch="core"),
dict(name="data0",     value="0x05", arch="core"),
dict(name="data1",     value="0x06", arch="core"),
dict(name="data2",     value="0x07", arch="core"),
dict(name="data3",     value="0x08", arch="core"),
dict(name="data4",     value="0x09", arch="core"),
dict(name="data5",     value="0x0A", arch="core"),
dict(name="data6",     value="0x0B", arch="core"),
dict(name="data7",     value="0x0C", arch="core"),


# ============================================================
# LOK / MFX / S88 / CONFIG COMMANDS
# ============================================================
dict(name="Sys_CMD",                value="0x00", arch="core"),
dict(name="LokDiscovery",           value="0x02", arch="core"),
dict(name="LokDiscovery_R",         value="0x03", arch="core"),
dict(name="Lok_Zyklus_Stopp",       value="0x04", arch="core"),
dict(name="MFXBind",                value="0x04", arch="core"),
dict(name="MFXBind_R",              value="0x05", arch="core"),
dict(name="MFXVerify",              value="0x06", arch="core"),
dict(name="MFXVerify_R",            value="0x07", arch="core"),
dict(name="Lok_Speed",              value="0x08", arch="core"),
dict(name="Lok_Direction",          value="0x0A", arch="core"),
dict(name="Lok_Function",           value="0x0C", arch="core"),
dict(name="ReadConfig",             value="0x0E", arch="core"),
dict(name="ReadConfig_R",           value="0x0F", arch="core"),
dict(name="WriteConfig",            value="0x10", arch="core"),
dict(name="WriteConfig_R",          value="0x11", arch="core"),
dict(name="SWITCH_ACC",             value="0x16", arch="core"),
dict(name="SWITCH_ACC_R",           value="0x17", arch="core"),
dict(name="S88_Polling",            value="0x20", arch="core"),
dict(name="S88_EVENT",              value="0x22", arch="core"),
dict(name="S88_EVENT_R",            value="0x23", arch="core"),
dict(name="PING",                   value="0x30", arch="core"),
dict(name="PING_R",                 value="0x31", arch="core"),
dict(name="CONFIG_Status",          value="0x3A", arch="core"),
dict(name="CONFIG_Status_R",        value="0x3B", arch="core"),
dict(name="DataQuery",              value="0x40", arch="core"),
dict(name="ConfigDataStream",       value="0x42", arch="core"),


# ============================================================
# PROTOCOL TYPES (MM / DCC)
# ============================================================
dict(name="MM_ACC",    value="0x3000", arch="core"),
dict(name="DCC_ACC",   value="0x3800", arch="core"),
dict(name="MM_TRACK",  value="0x0000", arch="core"),
dict(name="DCC_TRACK", value="0xC000", arch="core"),


# ============================================================
# DEVICE TYPES
# ============================================================
dict(name="DEVTYPE_GFP",          value="0x00", arch="core"),
dict(name="DEVTYPE_GB",           value="0x10", arch="core"),
dict(name="DEVTYPE_CONNECT",      value="0x20", arch="core"),
dict(name="DEVTYPE_MS2",          value="0x30", arch="core"),
dict(name="DEVTYPE_WDEV",         value="0xE0", arch="core"),
dict(name="DEVTYPE_CS2",          value="0xFF", arch="core"),
dict(name="DEVTYPE_FirstCANguru", value="0x4F", arch="core"),
dict(name="DEVTYPE_BASE",         value="0x50", arch="core"),
dict(name="DEVTYPE_SIGNAL",       value="0x51", arch="core"),
dict(name="DEVTYPE_SWITCH",       value="0x52", arch="core"),
dict(name="DEVTYPE_POWER",        value="0x53", arch="core"),
dict(name="DEVTYPE_RM",           value="0x54", arch="core"),
dict(name="DEVTYPE_MAXISIGNAL",   value="0x55", arch="core"),
dict(name="DEVTYPE_SERVO",        value="0x56", arch="core"),
dict(name="DEVTYPE_LIGHT",        value="0x57", arch="core"),
dict(name="DEVTYPE_LEDSIGNAL",    value="0x58", arch="core"),
dict(name="DEVTYPE_CAR_CAR",      value="0x59", arch="core"),
dict(name="DEVTYPE_CAR_RM",       value="0x5A", arch="core"),
dict(name="DEVTYPE_CAR_SERVO",    value="0x5B", arch="core"),
dict(name="DEVTYPE_TRAFFICLIGHT", value="0x5C", arch="core"),
dict(name="DEVTYPE_BRIDGE",       value="0x5D", arch="core"),
dict(name="DEVTYPE_FREE",         value="0x5E", arch="core"),
dict(name="DEVTYPE_LastCANguru",  value="0x5F", arch="core"),


# ============================================================
# PROTOKOLL VERSION 2 (Phase 2) - Beschreibung: 0400-Protocol/PROTOKOLL.md
# Präfix CG2_ (die Konstanten des alten Formats sind mit Phase 3 entfernt).
# ============================================================

# --- Nachrichtenkopf ---
dict(name="CG2_MAGIC",          value="0xC2", arch="core"),  # Byte 0: CANguru, Hauptversion 2
dict(name="CG2_PROTO_MINOR",    value="0",    arch="core"),  # Unterversion, in HELLO/DISCOVER_REPLY
dict(name="CG2_HEADER_SIZE",    value="8",    arch="core"),
dict(name="CG2_ESPNOW_MAX",     value="250",  arch="core"),  # max. Nachricht über ESP-NOW (Kopf + Nutzdaten)

# --- Flags (Byte 3) ---
dict(name="CG2_FLAG_ACK_REQ",   value="0x01", arch="core"),  # Quittung erbeten
dict(name="CG2_FLAG_RESPONSE",  value="0x02", arch="core"),  # Antwort / Quittung
dict(name="CG2_FLAG_ERROR",     value="0x04", arch="core"),  # Fehler, Nutzdaten[0] = Fehlercode
dict(name="CG2_ADDR_SHIFT",     value="4",    arch="core"),  # Adressierungsart in Bit 4-5
dict(name="CG2_ADDR_MASK",      value="0x30", arch="core"),
dict(name="CG2_ADDR_ID",        value="0",    arch="core"),  # Byte 5 = Decoder-ID
dict(name="CG2_ADDR_TYPE",      value="1",    arch="core"),  # Byte 5 = Decodertyp
dict(name="CG2_ADDR_ALL",       value="2",    arch="core"),  # alle Decoder

# --- Decoder-Feld (Byte 5) ---
dict(name="CG2_ID_BRIDGE",      value="0x00", arch="core"),  # Nachricht an/von der Bridge selbst

# --- Fehlercodes (Nutzdaten[0] bei CG2_FLAG_ERROR) ---
dict(name="CG2_ERR_UNKNOWN_FEATURE", value="0x01", arch="core"),
dict(name="CG2_ERR_UNKNOWN_COMMAND", value="0x02", arch="core"),
dict(name="CG2_ERR_BAD_LENGTH",      value="0x03", arch="core"),
dict(name="CG2_ERR_BAD_VALUE",       value="0x04", arch="core"),
dict(name="CG2_ERR_BUSY",            value="0x05", arch="core"),
dict(name="CG2_ERR_NOT_DELIVERED",   value="0x06", arch="core"),  # Bridge: Decoder antwortet nicht
dict(name="CG2_ERR_UNKNOWN_DECODER", value="0x07", arch="core"),  # Bridge: ID unbekannt

# --- Zustellung ---
dict(name="CG2_RETRY_COUNT",    value="3",    arch="core"),
dict(name="CG2_RETRY_MS",       value="150",  arch="core"),

# --- Feature 0x01 Sitzung ---
dict(name="CG2_F_SESSION",           value="0x01", arch="core"),
dict(name="CG2_SESSION_DISCOVER",    value="0x01", arch="core"),
dict(name="CG2_SESSION_DISCOVER_REPLY", value="0x02", arch="core"),
dict(name="CG2_SESSION_START",       value="0x03", arch="core"),
dict(name="CG2_SESSION_END",         value="0x04", arch="core"),
dict(name="CG2_SESSION_TRACK_POWER", value="0x05", arch="core"),  # Bridge -> alle Decoder: Gleisspannung [an 0/1]

# --- Feature 0x02 Decoder ---
dict(name="CG2_F_DECODER",           value="0x02", arch="core"),
dict(name="CG2_DECODER_HELLO",       value="0x01", arch="core"),
dict(name="CG2_DECODER_ASSIGN_ID",   value="0x02", arch="core"),
dict(name="CG2_DECODER_ADDED",       value="0x03", arch="core"),
dict(name="CG2_DECODER_REJOIN",      value="0x04", arch="core"),
dict(name="CG2_DECODER_REMOVE",      value="0x05", arch="core"),
dict(name="CG2_DECODER_RESET",       value="0x06", arch="core"),

# --- Feature 0x03 Alive ---
dict(name="CG2_F_ALIVE",             value="0x03", arch="core"),
dict(name="CG2_ALIVE_ALIVE",         value="0x01", arch="core"),
dict(name="CG2_ALIVE_SUMMARY",       value="0x02", arch="core"),
dict(name="CG2_STATUS_ERROR",        value="0",    arch="core"),
dict(name="CG2_STATUS_OK",           value="1",    arch="core"),
dict(name="CG2_STATUS_WARNING",      value="2",    arch="core"),

# --- Feature 0x04 Parameter (Selbstbeschreibung, für alle Decodertypen gleich) ---
dict(name="CG2_F_PARAM",             value="0x04", arch="core"),
dict(name="CG2_PARAM_DESCRIBE_GET",  value="0x01", arch="core"),
dict(name="CG2_PARAM_DESCRIBE",      value="0x02", arch="core"),
dict(name="CG2_PARAM_READ",          value="0x03", arch="core"),
dict(name="CG2_PARAM_WRITE",         value="0x04", arch="core"),
dict(name="CG2_PARAM_FACTORY_RESET", value="0x05", arch="core"),
dict(name="CG2_KIND_PARAM",          value="0",    arch="core"),  # Art der Beschreibung
dict(name="CG2_KIND_ACTION",         value="1",    arch="core"),
dict(name="CG2_KIND_VALUE",          value="2",    arch="core"),  # Messwert (Alive)
dict(name="CG2_TYPE_U8",             value="0",    arch="core"),  # Datentyp eines Parameters
dict(name="CG2_TYPE_U16",            value="1",    arch="core"),
dict(name="CG2_TYPE_CHOICE",         value="2",    arch="core"),
dict(name="CG2_TYPE_BOOL",           value="3",    arch="core"),

# --- Feature 0x05 Aktion ---
dict(name="CG2_F_ACTION",            value="0x05", arch="core"),
dict(name="CG2_ACTION_EXECUTE",      value="0x01", arch="core"),

# --- Feature 0x06 Ereignis ---
dict(name="CG2_F_EVENT",             value="0x06", arch="core"),
dict(name="CG2_EVENT_EVENT",         value="0x01", arch="core"),
dict(name="CG2_EV_POSITION",         value="0x01", arch="core"),  # Stellung geändert
dict(name="CG2_EV_OVERCURRENT",      value="0x02", arch="core"),  # Überstrom-Abschaltung
dict(name="CG2_EV_CALIBRATED",       value="0x03", arch="core"),  # Kalibrierung fertig

# --- Feature 0x07 CAN-Tunnel (Märklin-Frames, 13 Byte) ---
dict(name="CG2_F_CAN",               value="0x07", arch="core"),
dict(name="CG2_CAN_TO_TRACKBOX",     value="0x01", arch="core"),  # GUI -> Bridge -> CAN
dict(name="CG2_CAN_TO_GUI",          value="0x02", arch="core"),  # Bridge -> GUI (mit Folgenummer + Quelle)
dict(name="CG2_CAN_TO_DECODERS",     value="0x03", arch="core"),  # Bridge -> Decoder
dict(name="CG2_CAN_FROM_DECODER",    value="0x04", arch="core"),  # Decoder -> Bridge (-> WDP, GUI)

# --- Feature 0x08 Datei (lokomotive.cs2) ---
dict(name="CG2_F_FILE",              value="0x08", arch="core"),
dict(name="CG2_FILE_BLOCK",          value="0x01", arch="core"),
dict(name="CG2_FILE_END",            value="0x02", arch="core"),

# --- Feature 0x09 OTA ---
dict(name="CG2_F_OTA",               value="0x09", arch="core"),
dict(name="CG2_OTA_UPLOAD_BEGIN",    value="0x01", arch="core"),  # GUI -> Bridge
dict(name="CG2_OTA_UPLOAD_BLOCK",    value="0x02", arch="core"),  # GUI -> Bridge
dict(name="CG2_OTA_START",           value="0x03", arch="core"),  # GUI -> Bridge (einzeln/Gruppe)
dict(name="CG2_OTA_PROGRESS",        value="0x04", arch="core"),  # Bridge -> GUI
dict(name="CG2_OTA_RESULT",          value="0x05", arch="core"),  # Bridge -> GUI
dict(name="CG2_OTA_DECODER_START",   value="0x06", arch="core"),  # Bridge -> Decoder: WLAN-Download starten
dict(name="CG2_OTA_BLOCK_SIZE",      value="1024", arch="core"),
# Grund eines fehlgeschlagenen Decoder-Updates (REJOIN und RESULT)
dict(name="CG2_OTA_FAIL_NONE",       value="0x00", arch="core"),
dict(name="CG2_OTA_FAIL_WIFI",       value="0x01", arch="core"),  # keine Verbindung zum WLAN der Bridge
dict(name="CG2_OTA_FAIL_HTTP",       value="0x02", arch="core"),  # Datei nicht abrufbar
dict(name="CG2_OTA_FAIL_SIZE",       value="0x03", arch="core"),  # Datei zu groß / Update.begin
dict(name="CG2_OTA_FAIL_DOWNLOAD",   value="0x04", arch="core"),  # Download abgebrochen / unvollständig
dict(name="CG2_OTA_FAIL_INVALID",    value="0x05", arch="core"),  # keine gültige Firmware

# Die Übergangs-Features 0x0A Strom, 0x0B Stepper und 0x0C Rückmelder sind mit Phase 3
# entfallen (Parameter, Aktion, Ereignis). Die Nummern bleiben frei, damit ältere
# Nachrichten nicht falsch gedeutet werden.

]

# ------------------------------------------------------------
# Datei-Mapping
# ------------------------------------------------------------

ARCH_TO_HEADER = {
    "core":     "protocol_constants_core.h",
    "bridge":   "protocol_constants_bridge.h",
    "signal":   "protocol_constants_signal.h",
    "switch":   "protocol_constants_switch.h",
    "servo":    "protocol_constants_servo.h",
    "power": "protocol_constants_power.h",
    "can":      "protocol_constants_can.h",
    "maerklin": "protocol_constants_maerklin.h",
}

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
OUT_HEADERS = os.path.join(SCRIPT_DIR, "generated_constants")
OUT_YAML = os.path.join(SCRIPT_DIR, "protocol_constants.yaml")
OUT_CS = os.path.join(SCRIPT_DIR, "ProtocolConstants.cs")

HEADER_TEMPLATE = """#pragma once
// ------------------------------------------------------------
// Auto-generated by constants_manager.py
// DO NOT EDIT MANUALLY
// ------------------------------------------------------------

"""

# ------------------------------------------------------------
# Header-Generator
# ------------------------------------------------------------

def generate_headers():
    os.makedirs(OUT_HEADERS, exist_ok=True)
    buckets = {arch: [] for arch in ARCH_TO_HEADER.keys()}

    for c in CONSTANTS:
        buckets[c["arch"]].append(c)

    for arch, filename in ARCH_TO_HEADER.items():
        consts = buckets[arch]
        if not consts:
            continue
        path = os.path.join(OUT_HEADERS, filename)
        with open(path, "w", encoding="utf-8") as f:
            f.write(HEADER_TEMPLATE)
            for c in consts:
                f.write(f"#define {c['name']} {c['value']}\n")
        print(f"[C]  {path}")

# ------------------------------------------------------------
# YAML-Generator
# ------------------------------------------------------------

def generate_yaml():
    data = {
        "constants": {
            c["name"]: {"value": c["value"], "architecture": c["arch"]}
            for c in CONSTANTS
        }
    }
    with open(OUT_YAML, "w", encoding="utf-8") as f:
        yaml.safe_dump(data, f, sort_keys=False)
    print(f"[YAML] {OUT_YAML}")

# ------------------------------------------------------------
# C#-Generator
# ------------------------------------------------------------

def generate_cs():
    lines = []
    lines.append("// Auto-generated by constants_manager.py")
    lines.append("public static class ProtocolConstants")
    lines.append("{")
    for c in CONSTANTS:
        val = c["value"]
        lines.append(f"    public const int {c['name']} = {val};")
    lines.append("}")
    with open(OUT_CS, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    print(f"[C#] {OUT_CS}")

# ------------------------------------------------------------
# Main
# ------------------------------------------------------------

def main():
    generate_headers()
    generate_yaml()
    generate_cs()
    print("Fertig – Konstanten aus einer Quelle generiert.")

if __name__ == "__main__":
    main()
