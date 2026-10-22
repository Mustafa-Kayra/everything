namespace Everything.Core.Ntfs;

/// <summary>USN kaydının değişiklik nedenleri (USN_REASON_*).</summary>
[Flags]
public enum UsnReason : uint
{
    None = 0,
    DataOverwrite = 0x1,
    DataExtend = 0x2,
    DataTruncation = 0x4,
    FileCreate = 0x100,
    FileDelete = 0x200,
    RenameOldName = 0x1000,
    RenameNewName = 0x2000,
    BasicInfoChange = 0x8000,
    HardLinkChange = 0x10000,
    ReparsePointChange = 0x100000,
    Close = 0x80000000,

    /// <summary>Boyutu veya tarihi etkileyen içerik değişiklikleri.</summary>
    ContentChanges = DataOverwrite | DataExtend | DataTruncation | BasicInfoChange | HardLinkChange | ReparsePointChange,
}

/// <summary>Tek bir USN değişiklik kaydı. Dosya referansı = MFT kayıt no | (sıra no &lt;&lt; 48).</summary>
public readonly record struct UsnRecord(long Usn, long FileReference, long ParentReference, UsnReason Reason, string Name, bool IsDirectory);

public readonly record struct UsnJournalState(ulong JournalId, long FirstUsn, long NextUsn);

/// <summary>Günlük silinmiş/yeniden oluşturulmuş ya da kaydedilen konum artık günlükte yok: tam tarama gerekir.</summary>
public sealed class UsnJournalResetException : Exception
{
    public UsnJournalResetException(string message)
        : base(message)
    {
    }
}

/// <summary>Bir NTFS biriminin değişiklik günlüğü (USN Journal).</summary>
public interface IUsnJournal : IDisposable
{
    UsnJournalState Query();

    /// <summary>
    /// <paramref name="startUsn"/> konumundan itibaren kayıtları okur.
    /// Günlük kimliği farklıysa ya da konum günlükten düşmüşse <see cref="UsnJournalResetException"/> fırlatır.
    /// </summary>
    List<UsnRecord> Read(ulong journalId, long startUsn, out long nextUsn, CancellationToken cancellationToken);

    /// <summary>Dosya referansından güncel tam yolu bulur (ör. OpenFileById); bulunamazsa null.</summary>
    string? ResolvePath(long fileReference);
}
