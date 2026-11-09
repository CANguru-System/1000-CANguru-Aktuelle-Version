namespace CANguru.GUI.Forms
{
    // Spalten eines Decoders in der Decoderliste (DecoderListForm)
    public interface IDecoderListViewItem
    {
        (string Name, string Type, string Mac, string Id, string Details) ToListViewColumns();
    }
}
