namespace Library_Management_system.Rfid.D2184;

/// <summary>
/// Command codes for the UHF RFID Reader Serial Interface Protocol V3.1, as implemented by the
/// D2184 vendor SDK (Reader/ReaderMethod.cs) and documented in
/// "UHF RFID Reader Serial Interface Protocol.pdf".
///
/// Only the commands SMA LMS actually uses are listed. Nothing here is inferred - every value is
/// taken from the vendor SDK source or the protocol document.
/// </summary>
public static class D2184Command
{
    // Reader control and diagnostics
    public const byte Reset = 0x70;
    public const byte SetUartBaudrate = 0x71;
    public const byte GetFirmwareVersion = 0x72;
    public const byte SetReaderAddress = 0x73;
    public const byte SetWorkAntenna = 0x74;
    public const byte GetWorkAntenna = 0x75;
    public const byte SetOutputPower = 0x76;
    public const byte GetOutputPower = 0x77;
    public const byte SetFrequencyRegion = 0x78;
    public const byte GetFrequencyRegion = 0x79;
    public const byte SetBeeperMode = 0x7A;
    public const byte GetReaderTemperature = 0x7B;
    public const byte GetReaderIdentifier = 0x68;

    // Inventory
    public const byte Inventory = 0x80;             // buffered
    public const byte RealTimeInventory = 0x89;     // streams tags as they are seen
    public const byte FastSwitchAntInventory = 0x8A;
    public const byte CustomizedInventory = 0x8B;

    // Buffer management
    public const byte GetInventoryBuffer = 0x90;
    public const byte GetAndResetInventoryBuffer = 0x91;
    public const byte GetInventoryBufferTagCount = 0x92;
    public const byte ResetInventoryBuffer = 0x93;

    // Tag access
    public const byte ReadTag = 0x81;
    public const byte WriteTag = 0x82;
}

/// <summary>
/// Error codes from the protocol document's error code table (section 3).
/// Mapped to librarian-readable text - specification section 48 forbids showing raw technical
/// failures to end users.
/// </summary>
public static class D2184ErrorCode
{
    public const byte CommandSuccess = 0x10;
    public const byte CommandFail = 0x11;
    public const byte McuResetError = 0x20;
    public const byte CwOnError = 0x21;
    public const byte AntennaMissing = 0x22;
    public const byte WriteFlashError = 0x23;
    public const byte ReadFlashError = 0x24;
    public const byte SetOutputPowerError = 0x25;
    public const byte TagInventoryError = 0x31;
    public const byte TagReadError = 0x32;
    public const byte TagWriteError = 0x33;
    public const byte TagLockError = 0x34;
    public const byte TagKillError = 0x35;
    public const byte NoTagError = 0x36;
    public const byte InventoryOkButAccessFail = 0x37;
    public const byte BufferIsEmpty = 0x38;
    public const byte AccessOrPasswordError = 0x40;
    public const byte ParameterInvalid = 0x41;
    public const byte WordCountTooLong = 0x42;
    public const byte MemBankOutOfRange = 0x43;
    public const byte LockRegionOutOfRange = 0x44;
    public const byte LockActionOutOfRange = 0x45;
    public const byte ReaderAddressInvalid = 0x46;
    public const byte AntennaIdOutOfRange = 0x47;
    public const byte OutputPowerOutOfRange = 0x48;
    public const byte FrequencyRegionOutOfRange = 0x49;
    public const byte BaudRateOutOfRange = 0x4A;
    public const byte BeeperModeOutOfRange = 0x4B;

    /// <summary>The technical name, for logs and diagnostics only.</summary>
    public static string TechnicalName(byte code) => code switch
    {
        CommandSuccess => "command_success",
        CommandFail => "command_fail",
        McuResetError => "mcu_reset_error",
        CwOnError => "cw_on_error",
        AntennaMissing => "antenna_missing_error",
        WriteFlashError => "write_flash_error",
        ReadFlashError => "read_flash_error",
        SetOutputPowerError => "set_output_power_error",
        TagInventoryError => "tag_inventory_error",
        TagReadError => "tag_read_error",
        TagWriteError => "tag_write_error",
        TagLockError => "tag_lock_error",
        TagKillError => "tag_kill_error",
        NoTagError => "no_tag_error",
        InventoryOkButAccessFail => "inventory_ok_but_access_fail",
        BufferIsEmpty => "buffer_is_empty_error",
        AccessOrPasswordError => "access_or_password_error",
        ParameterInvalid => "parameter_invalid",
        WordCountTooLong => "parameter_invalid_wordCnt_too_long",
        MemBankOutOfRange => "parameter_invalid_membank_out_of_range",
        LockRegionOutOfRange => "parameter_invalid_lock_region_out_of_range",
        LockActionOutOfRange => "parameter_invalid_lock_action_out_of_range",
        ReaderAddressInvalid => "parameter_reader_address_invalid",
        AntennaIdOutOfRange => "parameter_invalid_antenna_id_out_of_range",
        OutputPowerOutOfRange => "parameter_invalid_output_power_out_of_range",
        FrequencyRegionOutOfRange => "parameter_invalid_frequency_region_out_of_range",
        BaudRateOutOfRange => "parameter_invalid_baudrate_out_of_range",
        BeeperModeOutOfRange => "parameter_beeper_mode_out_of_range",
        _ => $"unknown_error_0x{code:X2}"
    };

    /// <summary>
    /// Message safe to show a librarian at the circulation desk. Always ends with something
    /// they can act on (specification section 98).
    /// </summary>
    public static string FriendlyMessage(byte code) => code switch
    {
        NoTagError => "No tag detected. Place the item on the reader and try again.",
        AntennaMissing => "The reader's antenna is not connected. Check the antenna cable.",
        BufferIsEmpty => "The reader has no stored scans to report.",
        InventoryOkButAccessFail => "The tag was detected but could not be read. Reposition the item and try again.",
        TagInventoryError or TagReadError => "The tag could not be read. Reposition the item and try again.",
        AccessOrPasswordError => "This tag is access-protected and could not be read.",
        McuResetError or CwOnError => "The reader reported a hardware fault. Reconnect it, or switch to manual mode.",
        WriteFlashError or ReadFlashError => "The reader could not save its settings. Contact support.",
        CommandFail => "The reader could not complete the request. Try again, or switch to manual mode.",
        _ => "The reader reported an error. Try again, or switch to manual mode."
    };
}

/// <summary>Defaults from "D2184 Manual.pdf" section 1. Overridable per reader.</summary>
public static class D2184Defaults
{
    public const string IpAddress = "192.168.0.178";
    public const int TcpPort = 4001;
    public const int BaudRate = 115200;

    /// <summary>Broadcast/default reader address used by the vendor demo.</summary>
    public const byte ReaderAddress = 0x01;

    /// <summary>
    /// Repeat parameter for real-time inventory. 0xFF gives the shortest cycle
    /// (~30-50ms with a single tag in field), which suits a circulation desk.
    /// </summary>
    public const byte ShortestInventoryRepeat = 0xFF;
}
