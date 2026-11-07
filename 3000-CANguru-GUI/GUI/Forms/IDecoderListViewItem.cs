namespace CANguru.GUI.Forms
{
    // Spalten eines Decoders in der Decoderliste (DecoderListForm)
    public interface IDecoderListViewItem
    {
        (string Type, string Mac, string Id, string uid, string Details) ToListViewColumns();
    }
}
