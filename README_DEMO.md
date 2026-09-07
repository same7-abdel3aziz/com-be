# Competition Hashtag Demo Backend

Backend بسيط ومباشر حسب الـ SRS المصغر لمشروع Hashtag Competition.

## أهم القرارات

- لا يوجد `Post` API.
- الاسم المستخدم في الكود وقاعدة البيانات هو `Participation` فقط.
- لا يوجد `CreatePost`.
- public register لا يقبل role نهائيًا ولا يستطيع إنشاء Admin.
- إنشاء Admin/Supervisor/Judge يتم من `UsersController` بواسطة `SystemAdmin` فقط.
- كل Tweet يتم حفظه بالكامل في `RawJsonData`، مع استخراج الحقول الأساسية في أعمدة منفصلة.
- كل تقييم = سجل مستقل في `ParticipationScores`.
- لا يسمح لنفس القاضي بتقييم نفس المشاركة مرتين.
- `FinalScore`, `ScoreSum`, `ScoreCount` يتم تحديثهم وقت حفظ التقييم، وليس وقت التقرير.

## Demo Users

بعد أول تشغيل:

| Role | Email | Password |
|---|---|---|
| SystemAdmin | admin@demo.local | Admin@12345 |
| GeneralSupervisor | supervisor@demo.local | Supervisor@12345 |
| Judge | judge1@demo.local | Judge@12345 |
| Judge | judge2@demo.local | Judge@12345 |

## تشغيل المشروع

```bash
dotnet restore
dotnet run
```

ثم افتح Swagger:

```text
https://localhost:<port>/swagger
```

## TwitterAPI.io

المشروع يدعم endpoint:

```http
GET https://api.twitterapi.io/twitter/tweet/advanced_search
```

الإعدادات في `appsettings.json`:

```json
"TwitterApiIo": {
  "BaseUrl": "https://api.twitterapi.io",
  "ApiKey": "",
  "UseMock": true,
  "QueryType": "Top",
  "LookbackMinutes": 10
}
```

للتشغيل الحقيقي:

```json
"UseMock": false,
"ApiKey": "YOUR_API_KEY"
```

## Flow سريع للعرض

1. Login كـ admin.
2. GET `/api/competitions` وخذ `competitionId`.
3. POST `/api/ingestion/competitions/{competitionId}/run`.
4. GET `/api/participations`.
5. Login كـ judge1.
6. GET `/api/participations/judge-queue?competitionId=...`.
7. POST `/api/scores/participations/{participationId}`.
8. نفس المشاركة تختفي من queue لنفس القاضي.
9. Login كـ supervisor.
10. GET `/api/dashboard?competitionId=...`.
11. Download:
    - `/api/reports/top-scores/export.csv`
    - `/api/reports/participations/export.csv`
    - `/api/reports/scores/export.csv`
    - `/api/reports/raw-json/export.json`

## ملاحظات

- لا توجد Winner Files.
- لا يوجد Workflow معقد.
- لا يوجد Facebook.
- لا يوجد Dynamic Criteria؛ التقييم رقم واحد من 1 إلى 10.
