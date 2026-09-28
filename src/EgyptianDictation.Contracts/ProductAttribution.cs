namespace EgyptianDictation.Contracts;

public static class ProductAttribution
{
    public const string Owner = "د. زكريا ابو كبيره";
    public const string Expertise = "خبير مكافحة الجرائم المادية والرقمية";
    public const string Specialty = "الطب الشرعي - مصر";
    public const string Email = "zakariapharm@gmail.com";
    public const string Rights = "حقوق الملكية الفكرية © 2026 د. زكريا ابو كبيره";

    public static string FullNotice =>
        Rights + "\n" + Expertise + " | " + Specialty + "\n" + Email;
}
