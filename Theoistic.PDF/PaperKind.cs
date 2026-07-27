namespace Theoistic.PDF;

public enum PaperKind : int
{
    //
    // Summary:
    //     The paper size is defined by the user.
    Custom = 0,
    //
    // Summary:
    //     Letter paper (8.5 in. by 11 in.).
    Letter = 1,
    //
    // Summary:
    //     Letter small paper (8.5 in. by 11 in.).
    LetterSmall = 2,
    //
    // Summary:
    //     Tabloid paper (11 in. by 17 in.).
    Tabloid = 3,
    //
    // Summary:
    //     Ledger paper (17 in. by 11 in.).
    Ledger = 4,
    //
    // Summary:
    //     Legal paper (8.5 in. by 14 in.).
    Legal = 5,
    //
    // Summary:
    //     Statement paper (5.5 in. by 8.5 in.).
    Statement = 6,
    //
    // Summary:
    //     Executive paper (7.25 in. by 10.5 in.).
    Executive = 7,
    //
    // Summary:
    //     A3 paper (297 mm by 420 mm).
    A3 = 8,
    //
    // Summary:
    //     A4 paper (210 mm by 297 mm).
    A4 = 9,
    //
    // Summary:
    //     A4 small paper (210 mm by 297 mm).
    A4Small = 10,
    //
    // Summary:
    //     A5 paper (148 mm by 210 mm).
    A5 = 11,
    //
    // Summary:
    //     B4 paper (250 mm by 353 mm).
    B4 = 12,
    //
    // Summary:
    //     B5 paper (176 mm by 250 mm).
    B5 = 13,
    //
    // Summary:
    //     Folio paper (8.5 in. by 13 in.).
    Folio = 14,
    //
    // Summary:
    //     Quarto paper (215 mm by 275 mm).
    Quarto = 15,
    //
    // Summary:
    //     Standard paper (10 in. by 14 in.).
    Standard10x14 = 16,
    //
    // Summary:
    //     Standard paper (11 in. by 17 in.).
    Standard11x17 = 17,
    //
    // Summary:
    //     Note paper (8.5 in. by 11 in.).
    Note = 18,
    //
    // Summary:
    //     #9 envelope (3.875 in. by 8.875 in.).
    Number9Envelope = 19,
    //
    // Summary:
    //     #10 envelope (4.125 in. by 9.5 in.).
    Number10Envelope = 20,
    //
    // Summary:
    //     #11 envelope (4.5 in. by 10.375 in.).
    Number11Envelope = 21,
    //
    // Summary:
    //     #12 envelope (4.75 in. by 11 in.).
    Number12Envelope = 22,
    //
    // Summary:
    //     #14 envelope (5 in. by 11.5 in.).
    Number14Envelope = 23,
    //
    // Summary:
    //     C paper (17 in. by 22 in.).
    CSheet = 24,
    //
    // Summary:
    //     D paper (22 in. by 34 in.).
    DSheet = 25,
    //
    // Summary:
    //     E paper (34 in. by 44 in.).
    ESheet = 26,
    //
    // Summary:
    //     DL envelope (110 mm by 220 mm).
    DLEnvelope = 27,
    //
    // Summary:
    //     C5 envelope (162 mm by 229 mm).
    C5Envelope = 28,
    //
    // Summary:
    //     C3 envelope (324 mm by 458 mm).
    C3Envelope = 29,
    //
    // Summary:
    //     C4 envelope (229 mm by 324 mm).
    C4Envelope = 30,
    //
    // Summary:
    //     C6 envelope (114 mm by 162 mm).
    C6Envelope = 31,
    //
    // Summary:
    //     C65 envelope (114 mm by 229 mm).
    C65Envelope = 32,
    //
    // Summary:
    //     B4 envelope (250 mm by 353 mm).
    B4Envelope = 33,
    //
    // Summary:
    //     B5 envelope (176 mm by 250 mm).
    B5Envelope = 34,
    //
    // Summary:
    //     B6 envelope (176 mm by 125 mm).
    B6Envelope = 35,
    //
    // Summary:
    //     Italy envelope (110 mm by 230 mm).
    ItalyEnvelope = 36,
    //
    // Summary:
    //     Monarch envelope (3.875 in. by 7.5 in.).
    MonarchEnvelope = 37,
    //
    // Summary:
    //     6 3/4 envelope (3.625 in. by 6.5 in.).
    PersonalEnvelope = 38,
    //
    // Summary:
    //     US standard fanfold (14.875 in. by 11 in.).
    USStandardFanfold = 39,
    //
    // Summary:
    //     German standard fanfold (8.5 in. by 12 in.).
    GermanStandardFanfold = 40,
    //
    // Summary:
    //     German legal fanfold (8.5 in. by 13 in.).
    GermanLegalFanfold = 41,
    //
    // Summary:
    //     ISO B4 (250 mm by 353 mm).
    IsoB4 = 42,
    //
    // Summary:
    //     Japanese postcard (100 mm by 148 mm).
    JapanesePostcard = 43,
    //
    // Summary:
    //     Standard paper (9 in. by 11 in.).
    Standard9x11 = 44,
    //
    // Summary:
    //     Standard paper (10 in. by 11 in.).
    Standard10x11 = 45,
    //
    // Summary:
    //     Standard paper (15 in. by 11 in.).
    Standard15x11 = 46,
    //
    // Summary:
    //     Invitation envelope (220 mm by 220 mm).
    InviteEnvelope = 47,
    //
    // Summary:
    //     Letter extra paper (9.275 in. by 12 in.). This value is specific to the PostScript
    //     driver and is used only by Linotronic printers in order to conserve paper.
    LetterExtra = 50,
    //
    // Summary:
    //     Legal extra paper (9.275 in. by 15 in.). This value is specific to the PostScript
    //     driver and is used only by Linotronic printers in order to conserve paper.
    LegalExtra = 51,
    //
    // Summary:
    //     Tabloid extra paper (11.69 in. by 18 in.). This value is specific to the PostScript
    //     driver and is used only by Linotronic printers in order to conserve paper.
    TabloidExtra = 52,
    //
    // Summary:
    //     A4 extra paper (236 mm by 322 mm). This value is specific to the PostScript driver
    //     and is used only by Linotronic printers to help save paper.
    A4Extra = 53,
    //
    // Summary:
    //     Letter transverse paper (8.275 in. by 11 in.).
    LetterTransverse = 54,
    //
    // Summary:
    //     A4 transverse paper (210 mm by 297 mm).
    A4Transverse = 55,
    //
    // Summary:
    //     Letter extra transverse paper (9.275 in. by 12 in.).
    LetterExtraTransverse = 56,
    //
    // Summary:
    //     SuperA/SuperA/A4 paper (227 mm by 356 mm).
    APlus = 57,
    //
    // Summary:
    //     SuperB/SuperB/A3 paper (305 mm by 487 mm).
    BPlus = 58,
    //
    // Summary:
    //     Letter plus paper (8.5 in. by 12.69 in.).
    LetterPlus = 59,
    //
    // Summary:
    //     A4 plus paper (210 mm by 330 mm).
    A4Plus = 60,
    //
    // Summary:
    //     A5 transverse paper (148 mm by 210 mm).
    A5Transverse = 61,
    //
    // Summary:
    //     JIS B5 transverse paper (182 mm by 257 mm).
    B5Transverse = 62,
    //
    // Summary:
    //     A3 extra paper (322 mm by 445 mm).
    A3Extra = 63,
    //
    // Summary:
    //     A5 extra paper (174 mm by 235 mm).
    A5Extra = 64,
    //
    // Summary:
    //     ISO B5 extra paper (201 mm by 276 mm).
    B5Extra = 65,
    //
    // Summary:
    //     A2 paper (420 mm by 594 mm).
    A2 = 66,
    //
    // Summary:
    //     A3 transverse paper (297 mm by 420 mm).
    A3Transverse = 67,
    //
    // Summary:
    //     A3 extra transverse paper (322 mm by 445 mm).
    A3ExtraTransverse = 68,
    //
    // Summary:
    //     Japanese double postcard (200 mm by 148 mm). Requires Windows 98, Windows NT
    //     4.0, or later.
    JapaneseDoublePostcard = 69,
    //
    // Summary:
    //     A6 paper (105 mm by 148 mm). Requires Windows 98, Windows NT 4.0, or later.
    A6 = 70,
    //
    // Summary:
    //     Japanese Kaku #2 envelope. Requires Windows 98, Windows NT 4.0, or later.
    JapaneseEnvelopeKakuNumber2 = 71,
    //
    // Summary:
    //     Japanese Kaku #3 envelope. Requires Windows 98, Windows NT 4.0, or later.
    JapaneseEnvelopeKakuNumber3 = 72,
    //
    // Summary:
    //     Japanese Chou #3 envelope. Requires Windows 98, Windows NT 4.0, or later.
    JapaneseEnvelopeChouNumber3 = 73,
    //
    // Summary:
    //     Japanese Chou #4 envelope. Requires Windows 98, Windows NT 4.0, or later.
    JapaneseEnvelopeChouNumber4 = 74,
    //
    // Summary:
    //     Letter rotated paper (11 in. by 8.5 in.).
    LetterRotated = 75,
    //
    // Summary:
    //     A3 rotated paper (420 mm by 297 mm).
    A3Rotated = 76,
    //
    // Summary:
    //     A4 rotated paper (297 mm by 210 mm). Requires Windows 98, Windows NT 4.0, or
    //     later.
    A4Rotated = 77,
    //
    // Summary:
    //     A5 rotated paper (210 mm by 148 mm). Requires Windows 98, Windows NT 4.0, or
    //     later.
    A5Rotated = 78,
    //
    // Summary:
    //     JIS B4 rotated paper (364 mm by 257 mm). Requires Windows 98, Windows NT 4.0,
    //     or later.
    B4JisRotated = 79,
    //
    // Summary:
    //     JIS B5 rotated paper (257 mm by 182 mm). Requires Windows 98, Windows NT 4.0,
    //     or later.
    B5JisRotated = 80,
    //
    // Summary:
    //     Japanese rotated postcard (148 mm by 100 mm). Requires Windows 98, Windows NT
    //     4.0, or later.
    JapanesePostcardRotated = 81,
    //
    // Summary:
    //     Japanese rotated double postcard (148 mm by 200 mm). Requires Windows 98, Windows
    //     NT 4.0, or later.
    JapaneseDoublePostcardRotated = 82,
    //
    // Summary:
    //     A6 rotated paper (148 mm by 105 mm). Requires Windows 98, Windows NT 4.0, or
    //     later.
    A6Rotated = 83,
    //
    // Summary:
    //     Japanese rotated Kaku #2 envelope. Requires Windows 98, Windows NT 4.0, or later.
    JapaneseEnvelopeKakuNumber2Rotated = 84,
    //
    // Summary:
    //     Japanese rotated Kaku #3 envelope. Requires Windows 98, Windows NT 4.0, or later.
    JapaneseEnvelopeKakuNumber3Rotated = 85,
    //
    // Summary:
    //     Japanese rotated Chou #3 envelope. Requires Windows 98, Windows NT 4.0, or later.
    JapaneseEnvelopeChouNumber3Rotated = 86,
    //
    // Summary:
    //     Japanese rotated Chou #4 envelope. Requires Windows 98, Windows NT 4.0, or later.
    JapaneseEnvelopeChouNumber4Rotated = 87,
    //
    // Summary:
    //     JIS B6 paper (128 mm by 182 mm). Requires Windows 98, Windows NT 4.0, or later.
    B6Jis = 88,
    //
    // Summary:
    //     JIS B6 rotated paper (182 mm by 128 mm). Requires Windows 98, Windows NT 4.0,
    //     or later.
    B6JisRotated = 89,
    //
    // Summary:
    //     Standard paper (12 in. by 11 in.). Requires Windows 98, Windows NT 4.0, or later.
    Standard12x11 = 90,
    //
    // Summary:
    //     Japanese You #4 envelope. Requires Windows 98, Windows NT 4.0, or later.
    JapaneseEnvelopeYouNumber4 = 91,
    //
    // Summary:
    //     Japanese You #4 rotated envelope. Requires Windows 98, Windows NT 4.0, or later.
    JapaneseEnvelopeYouNumber4Rotated = 92,
    //
    // Summary:
    //     People's Republic of China 16K paper (146 mm by 215 mm). Requires Windows 98,
    //     Windows NT 4.0, or later.
    Prc16K = 93,
    //
    // Summary:
    //     People's Republic of China 32K paper (97 mm by 151 mm). Requires Windows 98,
    //     Windows NT 4.0, or later.
    Prc32K = 94,
    //
    // Summary:
    //     People's Republic of China 32K big paper (97 mm by 151 mm). Requires Windows
    //     98, Windows NT 4.0, or later.
    Prc32KBig = 95,
    //
    // Summary:
    //     People's Republic of China #1 envelope (102 mm by 165 mm). Requires Windows 98,
    //     Windows NT 4.0, or later.
    PrcEnvelopeNumber1 = 96,
    //
    // Summary:
    //     People's Republic of China #2 envelope (102 mm by 176 mm). Requires Windows 98,
    //     Windows NT 4.0, or later.
    PrcEnvelopeNumber2 = 97,
    //
    // Summary:
    //     People's Republic of China #3 envelope (125 mm by 176 mm). Requires Windows 98,
    //     Windows NT 4.0, or later.
    PrcEnvelopeNumber3 = 98,
    //
    // Summary:
    //     People's Republic of China #4 envelope (110 mm by 208 mm). Requires Windows 98,
    //     Windows NT 4.0, or later.
    PrcEnvelopeNumber4 = 99,
    //
    // Summary:
    //     People's Republic of China #5 envelope (110 mm by 220 mm). Requires Windows 98,
    //     Windows NT 4.0, or later.
    PrcEnvelopeNumber5 = 100,
    //
    // Summary:
    //     People's Republic of China #6 envelope (120 mm by 230 mm). Requires Windows 98,
    //     Windows NT 4.0, or later.
    PrcEnvelopeNumber6 = 101,
    //
    // Summary:
    //     People's Republic of China #7 envelope (160 mm by 230 mm). Requires Windows 98,
    //     Windows NT 4.0, or later.
    PrcEnvelopeNumber7 = 102,
    //
    // Summary:
    //     People's Republic of China #8 envelope (120 mm by 309 mm). Requires Windows 98,
    //     Windows NT 4.0, or later.
    PrcEnvelopeNumber8 = 103,
    //
    // Summary:
    //     People's Republic of China #9 envelope (229 mm by 324 mm). Requires Windows 98,
    //     Windows NT 4.0, or later.
    PrcEnvelopeNumber9 = 104,
    //
    // Summary:
    //     People's Republic of China #10 envelope (324 mm by 458 mm). Requires Windows
    //     98, Windows NT 4.0, or later.
    PrcEnvelopeNumber10 = 105,
    //
    // Summary:
    //     People's Republic of China 16K rotated paper (146 mm by 215 mm). Requires Windows
    //     98, Windows NT 4.0, or later.
    Prc16KRotated = 106,
    //
    // Summary:
    //     People's Republic of China 32K rotated paper (97 mm by 151 mm). Requires Windows
    //     98, Windows NT 4.0, or later.
    Prc32KRotated = 107,
    //
    // Summary:
    //     People's Republic of China 32K big rotated paper (97 mm by 151 mm). Requires
    //     Windows 98, Windows NT 4.0, or later.
    Prc32KBigRotated = 108,
    //
    // Summary:
    //     People's Republic of China #1 rotated envelope (165 mm by 102 mm). Requires Windows
    //     98, Windows NT 4.0, or later.
    PrcEnvelopeNumber1Rotated = 109,
    //
    // Summary:
    //     People's Republic of China #2 rotated envelope (176 mm by 102 mm). Requires Windows
    //     98, Windows NT 4.0, or later.
    PrcEnvelopeNumber2Rotated = 110,
    //
    // Summary:
    //     People's Republic of China #3 rotated envelope (176 mm by 125 mm). Requires Windows
    //     98, Windows NT 4.0, or later.
    PrcEnvelopeNumber3Rotated = 111,
    //
    // Summary:
    //     People's Republic of China #4 rotated envelope (208 mm by 110 mm). Requires Windows
    //     98, Windows NT 4.0, or later.
    PrcEnvelopeNumber4Rotated = 112,
    //
    // Summary:
    //     People's Republic of China Envelope #5 rotated envelope (220 mm by 110 mm). Requires
    //     Windows 98, Windows NT 4.0, or later.
    PrcEnvelopeNumber5Rotated = 113,
    //
    // Summary:
    //     People's Republic of China #6 rotated envelope (230 mm by 120 mm). Requires Windows
    //     98, Windows NT 4.0, or later.
    PrcEnvelopeNumber6Rotated = 114,
    //
    // Summary:
    //     People's Republic of China #7 rotated envelope (230 mm by 160 mm). Requires Windows
    //     98, Windows NT 4.0, or later.
    PrcEnvelopeNumber7Rotated = 115,
    //
    // Summary:
    //     People's Republic of China #8 rotated envelope (309 mm by 120 mm). Requires Windows
    //     98, Windows NT 4.0, or later.
    PrcEnvelopeNumber8Rotated = 116,
    //
    // Summary:
    //     People's Republic of China #9 rotated envelope (324 mm by 229 mm). Requires Windows
    //     98, Windows NT 4.0, or later.
    PrcEnvelopeNumber9Rotated = 117,
    //
    // Summary:
    //     People's Republic of China #10 rotated envelope (458 mm by 324 mm). Requires
    //     Windows 98, Windows NT 4.0, or later.
    PrcEnvelopeNumber10Rotated = 118
}

public class PechkinPaperSize
{
    private static readonly Dictionary<PaperKind, PechkinPaperSize> dictionary = new Dictionary<PaperKind, PechkinPaperSize>()
    {
        // paper sizes from http://msdn.microsoft.com/en-us/library/system.drawing.printing.paperkind.aspx
        { PaperKind.Letter, new PechkinPaperSize("8.5in", "11in") },
        { PaperKind.Legal, new PechkinPaperSize("8.5in", "14in") },
        { PaperKind.A4, new PechkinPaperSize("210mm", "297mm") },
        { PaperKind.CSheet, new PechkinPaperSize("17in", "22in") },
        { PaperKind.DSheet, new PechkinPaperSize("22in", "34in") },
        { PaperKind.ESheet, new PechkinPaperSize("34in", "44in") },
        { PaperKind.LetterSmall, new PechkinPaperSize("8.5in", "11in") },
        { PaperKind.Tabloid, new PechkinPaperSize("11in", "17in") },
        { PaperKind.Ledger, new PechkinPaperSize("17in", "11in") },
        { PaperKind.Statement, new PechkinPaperSize("5.5in", "8.5in") },
        { PaperKind.Executive, new PechkinPaperSize("7.25in", "10.5in") },
        { PaperKind.A3, new PechkinPaperSize("297mm", "420mm") },
        { PaperKind.A4Small, new PechkinPaperSize("210mm", "297mm") },
        { PaperKind.A5, new PechkinPaperSize("148mm", "210mm") },
        { PaperKind.B4, new PechkinPaperSize("250mm", "353mm") },
        { PaperKind.B5, new PechkinPaperSize("176mm", "250mm") },
        { PaperKind.Folio, new PechkinPaperSize("8.5in", "13in") },
        { PaperKind.Quarto, new PechkinPaperSize("215mm", "275mm") },
        { PaperKind.Standard10x14, new PechkinPaperSize("10in", "14in") },
        { PaperKind.Standard11x17, new PechkinPaperSize("11in", "17in") },
        { PaperKind.Note, new PechkinPaperSize("8.5in", "11in") },
        { PaperKind.Number9Envelope, new PechkinPaperSize("3.875in", "8.875in") },
        { PaperKind.Number10Envelope, new PechkinPaperSize("4.125in", "9.5in") },
        { PaperKind.Number11Envelope, new PechkinPaperSize("4.5in", "10.375in") },
        { PaperKind.Number12Envelope, new PechkinPaperSize("4.75in", "11in") },
        { PaperKind.Number14Envelope, new PechkinPaperSize("5in", "11.5in") },
        { PaperKind.DLEnvelope, new PechkinPaperSize("110mm", "220mm") },
        { PaperKind.C5Envelope, new PechkinPaperSize("162mm", "229mm") },
        { PaperKind.C3Envelope, new PechkinPaperSize("324mm", "458mm") },
        { PaperKind.C4Envelope, new PechkinPaperSize("229mm", "324mm") },
        { PaperKind.C6Envelope, new PechkinPaperSize("114mm", "162mm") },
        { PaperKind.C65Envelope, new PechkinPaperSize("114mm", "229mm") },
        { PaperKind.B4Envelope, new PechkinPaperSize("250mm", "353mm") },
        { PaperKind.B5Envelope, new PechkinPaperSize("176mm", "250mm") },
        { PaperKind.B6Envelope, new PechkinPaperSize("176mm", "125mm") },
        { PaperKind.ItalyEnvelope, new PechkinPaperSize("110mm", "230mm") },
        { PaperKind.MonarchEnvelope, new PechkinPaperSize("3.875in", "7.5in") },
        { PaperKind.PersonalEnvelope, new PechkinPaperSize("3.625in", "6.5in") },
        { PaperKind.USStandardFanfold, new PechkinPaperSize("14.875in", "11in") },
        { PaperKind.GermanStandardFanfold, new PechkinPaperSize("8.5in", "12in") },
        { PaperKind.GermanLegalFanfold, new PechkinPaperSize("8.5in", "13in") },
        { PaperKind.IsoB4, new PechkinPaperSize("250mm", "353mm") },
        { PaperKind.JapanesePostcard, new PechkinPaperSize("100mm", "148mm") },
        { PaperKind.Standard9x11, new PechkinPaperSize("9in", "11in") },
        { PaperKind.Standard10x11, new PechkinPaperSize("10in", "11in") },
        { PaperKind.Standard15x11, new PechkinPaperSize("15in", "11in") },
        { PaperKind.InviteEnvelope, new PechkinPaperSize("220mm", "220mm") },
        { PaperKind.LetterExtra, new PechkinPaperSize("9.275in", "12in") },
        { PaperKind.LegalExtra, new PechkinPaperSize("9.275in", "15in") },
        { PaperKind.TabloidExtra, new PechkinPaperSize("11.69in", "18in") },
        { PaperKind.A4Extra, new PechkinPaperSize("236mm", "322mm") },
        { PaperKind.LetterTransverse, new PechkinPaperSize("8.275in", "11in") },
        { PaperKind.A4Transverse, new PechkinPaperSize("210mm", "297mm") },
        { PaperKind.LetterExtraTransverse, new PechkinPaperSize("9.275in", "12in") },
        { PaperKind.APlus, new PechkinPaperSize("227mm", "356mm") },
        { PaperKind.BPlus, new PechkinPaperSize("305mm", "487mm") },
        { PaperKind.LetterPlus, new PechkinPaperSize("8.5in", "12.69in") },
        { PaperKind.A4Plus, new PechkinPaperSize("210mm", "330mm") },
        { PaperKind.A5Transverse, new PechkinPaperSize("148mm", "210mm") },
        { PaperKind.B5Transverse, new PechkinPaperSize("182mm", "257mm") },
        { PaperKind.A3Extra, new PechkinPaperSize("322mm", "445mm") },
        { PaperKind.A5Extra, new PechkinPaperSize("174mm", "235mm") },
        { PaperKind.B5Extra, new PechkinPaperSize("201mm", "276mm") },
        { PaperKind.A2, new PechkinPaperSize("420mm", "594mm") },
        { PaperKind.A3Transverse, new PechkinPaperSize("297mm", "420mm") },
        { PaperKind.A3ExtraTransverse, new PechkinPaperSize("322mm", "445mm") },
        { PaperKind.JapaneseDoublePostcard, new PechkinPaperSize("200mm", "148mm") },
        { PaperKind.A6, new PechkinPaperSize("105mm", "148mm") },
        { PaperKind.LetterRotated, new PechkinPaperSize("11in", "8.5in") },
        { PaperKind.A3Rotated, new PechkinPaperSize("420mm", "297mm") },
        { PaperKind.A4Rotated, new PechkinPaperSize("297mm", "210mm") },
        { PaperKind.A5Rotated, new PechkinPaperSize("210mm", "148mm") },
        { PaperKind.B4JisRotated, new PechkinPaperSize("364mm", "257mm") },
        { PaperKind.B5JisRotated, new PechkinPaperSize("257mm", "182mm") },
        { PaperKind.JapanesePostcardRotated, new PechkinPaperSize("148mm", "100mm") },
        { PaperKind.JapaneseDoublePostcardRotated, new PechkinPaperSize("148mm", "200mm") },
        { PaperKind.A6Rotated, new PechkinPaperSize("148mm", "105mm") },
        { PaperKind.B6Jis, new PechkinPaperSize("128mm", "182mm") },
        { PaperKind.B6JisRotated, new PechkinPaperSize("182mm", "128mm") },
        { PaperKind.Standard12x11, new PechkinPaperSize("12in", "11in") },
        { PaperKind.Prc16K, new PechkinPaperSize("146mm", "215mm") },
        { PaperKind.Prc32K, new PechkinPaperSize("97mm", "151mm") },
        { PaperKind.Prc32KBig, new PechkinPaperSize("97mm", "151mm") },
        { PaperKind.PrcEnvelopeNumber1, new PechkinPaperSize("102mm", "165mm") },
        { PaperKind.PrcEnvelopeNumber2, new PechkinPaperSize("102mm", "176mm") },
        { PaperKind.PrcEnvelopeNumber3, new PechkinPaperSize("125mm", "176mm") },
        { PaperKind.PrcEnvelopeNumber4, new PechkinPaperSize("110mm", "208mm") },
        { PaperKind.PrcEnvelopeNumber5, new PechkinPaperSize("110mm", "220mm") },
        { PaperKind.PrcEnvelopeNumber6, new PechkinPaperSize("120mm", "230mm") },
        { PaperKind.PrcEnvelopeNumber7, new PechkinPaperSize("160mm", "230mm") },
        { PaperKind.PrcEnvelopeNumber8, new PechkinPaperSize("120mm", "309mm") },
        { PaperKind.PrcEnvelopeNumber9, new PechkinPaperSize("229mm", "324mm") },
        { PaperKind.PrcEnvelopeNumber10, new PechkinPaperSize("324mm", "458mm") },
        { PaperKind.Prc16KRotated, new PechkinPaperSize("146mm", "215mm") },
        { PaperKind.Prc32KRotated, new PechkinPaperSize("97mm", "151mm") },
        { PaperKind.Prc32KBigRotated, new PechkinPaperSize("97mm", "151mm") },
        { PaperKind.PrcEnvelopeNumber1Rotated, new PechkinPaperSize("165mm", "102mm") },
        { PaperKind.PrcEnvelopeNumber2Rotated, new PechkinPaperSize("176mm", "102mm") },
        { PaperKind.PrcEnvelopeNumber3Rotated, new PechkinPaperSize("176mm", "125mm") },
        { PaperKind.PrcEnvelopeNumber4Rotated, new PechkinPaperSize("208mm", "110mm") },
        { PaperKind.PrcEnvelopeNumber5Rotated, new PechkinPaperSize("220mm", "110mm") },
        { PaperKind.PrcEnvelopeNumber6Rotated, new PechkinPaperSize("230mm", "120mm") },
        { PaperKind.PrcEnvelopeNumber7Rotated, new PechkinPaperSize("230mm", "160mm") },
        { PaperKind.PrcEnvelopeNumber8Rotated, new PechkinPaperSize("309mm", "120mm") },
        { PaperKind.PrcEnvelopeNumber9Rotated, new PechkinPaperSize("324mm", "229mm") },
        { PaperKind.PrcEnvelopeNumber10Rotated, new PechkinPaperSize("458mm", "324mm") },
    };

    public PechkinPaperSize(string width, string height)
    {
        this.Width = width;
        this.Height = height;
    }

    public string Height { get; set; }

    public string Width { get; set; }

    public static implicit operator PechkinPaperSize(PaperKind paperKind)
    {
        if (!dictionary.TryGetValue(paperKind, out var size))
        {
            throw new ArgumentOutOfRangeException(nameof(paperKind), paperKind,
                $"No paper dimensions are defined for {paperKind}. Set PaperSize to a new PechkinPaperSize(width, height) instead.");
        }

        // A copy, so that mutating GlobalSettings.PaperSize cannot rewrite the shared table
        // and change the dimensions of every other document in the process.
        return new PechkinPaperSize(size.Width, size.Height);
    }
}
