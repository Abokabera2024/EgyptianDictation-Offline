# تقرير حزمة المعاينة الإصدارية v2.3

التاريخ: 27 سبتمبر 2026

## الحزمة الكاملة

- الملف: `artifacts/release/EgyptianDictation-Setup-Full-v2.3.exe`
- الحجم: `2,202,168,009` بايت.
- SHA-256: `E59A0FA5BC699D59CEAD0131757B38EBF4B218B079D9517EB1E9EF0F42CD27B5`.
- تشمل التطبيق والعامل وإضافة Word والنموذج ومكتبات CPU/Vulkan الأصلية ومثبتَي
  Microsoft Visual C++ x64 و.NET Framework 4.8 غير المتصلين.
- فحص `--verify`: ناجح؛ النموذج المضمّن بحجم `1,770,270,112` بايت وبصمة
  `55E61C9B047E36F0E084D367F6B0BFECC71A6A0527DA6EEA4F0C687F3584775F`.

## حزمة تحديث التطبيق فقط

- الملف: `artifacts/release/EgyptianDictation-Setup-App-v2.3.exe`
- الحجم: `431,626,953` بايت.
- SHA-256: `B18B9DCF392A190C47319ADD57A847B05A24E0B2F8E721D19DB0D5A3AE2DD706`.
- تشمل مكتبات التشغيل نفسها، لكن لا تشمل النموذج. يرفض المثبت التحديث إذا لم
  يجد نموذجًا صحيحًا موجودًا مسبقًا.

## الاعتمادات الخارجية

- Visual C++ x64 `14.51.36247.0` من Microsoft، بصمة الملف
  `843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C`.
- .NET Framework 4.8 offline من Microsoft، بصمة الملف
  `0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40`.
- جرى التحقق من التوقيع الرقمي الرسمي لكلا المثبتين قبل بناء الحزمة. يفحص
  المثبت وجود إصدار كافٍ قبل تشغيلهما ولا يُنزّل شيئًا أثناء التثبيت.
- التطبيق والعامل منشوران بـ.NET 8 بصورة `self-contained`، لكن إضافة Word
  تستخدم .NET Framework 4.8، ويجهز المثبت نسخته غير المتصلة عند الحاجة.

## الاختبارات والحدود

- 56 اختبارًا عاديًا ناجحًا، واختبار النموذج الحقيقي على CPU ناجح، واختبار
  اختيار GPU/CPU التلقائي ناجح.
- فحص الحزمتين من داخل ملفات EXE الناتجة `--verify`: ناجح.
- لم يُجرَ تثبيت فعلي على جهاز نظيف غير متصل بالشبكة ضمن هذه الجولة؛ لذلك هذه
  حزمة مرشحة للتجربة وليست شهادة توافق مع كل جهاز.
- الهدف Windows 10/11 x64 مع Word Desktop. الأجهزة x86/ARM وWindows 7/8.1
  ليست مدعومة بهذه الحزمة. قد يلزم إعادة تشغيل Windows بعد تثبيت اعتماد جديد.
- ملف المثبت نفسه غير موقّع رقميًا. قبل النشر العام، ينبغي اختبار جهاز نظيف
  وتوقيع المثبت والتحقق من أهلية إعادة توزيع Visual C++ بموجب شروط Microsoft.

المراجع الرسمية: [Visual C++ Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)،
[نشر .NET Framework غير المتصل](https://learn.microsoft.com/en-us/dotnet/framework/deployment/deployment-guide-for-developers)،
[أنظمة .NET المدعومة](https://learn.microsoft.com/en-us/dotnet/core/install/windows).
