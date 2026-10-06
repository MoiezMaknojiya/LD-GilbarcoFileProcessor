# LdPosService Code Review Notes

Shuru: 2026-10-06. Repo: https://github.com/MoiezMaknojiya/LD-GilbarcoFileProcessor

Yeh file har review point ka masla, discussion, faisla aur status rakhti hai. Har point discuss hone ke baad yahan update hota hai. Line numbers us waqt ke hain jab point likha gaya; code badalne pe shift ho sakte hain.

## Status ka matlab

- **DONE**: fix ho gaya, commit mein hai
- **CHOR DO**: masla samjha, lekin jaan ke aise hi rakha (intended behaviour)
- **PENDING**: abhi discuss nahi hua

## Ek nazar mein

| # | Point | Severity | Status | Commit |
|---|---|---|---|---|
| 1 | Upload sirf nayi file pe trigger hota tha | Bara | DONE | 04597f3 |
| 2 | Logout pe saari pending transactions delete | Bara | CHOR DO | - |
| 3 | POS folder ki originals kabhi delete nahi, restart pe sab dobara process | Bara | AADHA: delete CHOR DO, duplicate upload + Modisoft race PENDING | |
| 4 | Watcher pehli baar fail ho to service hamesha phansi | Bara | PENDING | |
| 5 | Auto-login token verify nahi karta | Bara | PENDING | |
| 6 | DeptId 0 ka risk | Bara | PENDING | |
| 7 | Network-error break wala code dead tha | Darmiyana | DONE (point 1 ke saath) | 04597f3 |
| 8 | Watcher sirf Created event sunta hai | Darmiyana | PENDING | |
| 9 | Service mein Console.WriteLine | Darmiyana | PENDING | |
| 10 | Login JSON haath se jora hua | Darmiyana | PENDING | |
| 11 | Retry comment aur code alag | Darmiyana | PENDING | |
| 12 | Nested form chain | Chhota | PENDING | |
| 13 | UI freeze, WaitForStatus UI thread pe | Chhota | PENDING | |
| 14 | UNC path pe sync Directory.Exists | Chhota | PENDING | |
| 15 | Dead code aur faltu saaman | Chhota | PENDING | |

## Points tafseel se

### 1. Upload sirf nayi lottery file pe trigger hota tha — DONE (04597f3)

**Masla:** Upload sirf `ProcessFileAsync` ke step 12 se chalta tha. Transaction DB mein phansi reh sakti thi, do wajah se: doosri file ka upload chal raha tha jab yeh save hui (2 Passport ek saath finish), ya net down tha jab pehli baar try hui. Agli lottery sale tak koi nahi uthata tha. Saath mein `_isUploading` bool ka check aur set alag steps the, rare double upload mumkin.

**Discussion:** Moiez: store mein 2 Passport hain, max 2 files ek saath. Sahi, lekin race 2 files pe bhi hoti hai. Nayi file B ko apne saath le jaati hai, to loss nahi, delay hai. Delay ka worst case: din ki aakhri do sales saath saath to ek raat bhar latki, ya net wapas aane ke baad koi lottery sale na ho. Note: nayi file lottery wali honi chahiye, non-lottery XML dept filter pe delete ho jaati hai aur upload tak nahi pohnchti. Feb 2026 wale version mein 30-minute timer tha jo google.com HEAD check pe depend karta tha, March 2026 mein hata diya gaya.

**Faisla:** Fix karo. Timer wapas, lekin google.com ke bagair, seedha asli API pe try.

**Kya badla:**
- `LdFileProcessor/FileMonitorService.cs:39` `UploadRetryInterval` = 1 minute. Line 95 ka loop har minute `RetryPendingUploadsAsync` (line 655) chalata hai. DB mein pending na ho to chup rehta hai, log mein shor nahi.
- Line 561: bool flag ki jagah `SemaphoreSlim` aur `Wait(0)`. Check aur take ek atomic step.
- Line 609: pehli fail pe upload pass break. Chaar dead catch blocks (HttpRequestException, TaskCanceled, Socket, Win32) hata diye, woh kabhi chal hi nahi sakte the.
- `ApiLibrary/DatabaseServices.cs:362` naya `CountUnprocessedTransactions()`, sasta COUNT query.
- Purana commented 30-second folder refresh block nikaal diya.

**Ab behaviour:** B wali stranded transaction max 1 minute late. Net wapas aaye to 1 minute ke andar sab pending nikal jaati hain. Net down aur 20 pending hon to pehli fail pe pass band, agle minute dobara.

**Deploy:** Sirf code mein hai. Store machine pe service dobara publish aur restart zaroori.

### 2. Logout pe saari pending transactions delete — CHOR DO

**Masla:** `LdPosService/DashboardForm.cs:39` logout pe `DeleteAllTransactions()`. Offline logout ho to jo transactions abhi upload nahi hui thin woh bhi ud jaati hain.

**Faisla (Moiez, 2026-10-06):** Yeh intended hai. Logout matlab us user ka local data saaf. Aise hi rehne do.

### 3. POS folder ki originals kabhi delete nahi, restart pe sab dobara process — PENDING

**Masla:** Service original XML ko POS folder se kabhi delete nahi karti. `FileMonitorService.cs:274` sirf temp mein copy karta hai, aur delete sirf temp file ki hoti hai. Service restart ya network wapas aane pe line 88 aur 347 se `CopyExistingFilesAsync` folder ki har XML dobara copy (line 393) aur process karti hai. Pehle se uploaded wali bhi, kyunke unki DB row upload ke baad delete ho chuki hoti hai aur `INSERT OR IGNORE` unhe nayi row samajh ke dobara daal deta hai. Nateeja: duplicate upload.

**Faisla (Moiez, 2026-10-06), delete wala hissa: CHOR DO.** Usi folder se ek aur software, Modisoft, bhi padhta hai aur wohi files delete karta hai. Hum delete karein to Modisoft padh nahi payega. Passport bhi har 7 din mein folder khud khali kar deta hai. To originals ko haath nahi lagana, yeh intended hai.

**Abhi khula, hissa (b): restart pe duplicate upload.** Yeh delete se alag masla hai. Jab tak file folder mein hai (Modisoft ke delete tak, zyada se zyada 7 din), service restart ya network wapas aane pe woh file dobara DB mein jayegi aur dobara upload hogi, chahe pehle upload ho chuki ho. Browse Folder dabana bhi service restart hai.
- Agar server `check-json` pe same TransactionID dobara aaye to reject karta hai: poora point CHOR DO.
- Agar nahi: fix idea yeh hai ke upload success pe row delete na karein, `IsProcessed = 1` karein (column pehle se hai, kabhi use nahi hua) aur 14 din tak rakhein. Phir `INSERT OR IGNORE` purani TransId ko khud skip karega. Shart: TransactionID dono Passports mein unique ho. Agar dono registers same number de sakte hain to FileName pe dedupe karna padega.
- Sawal: server dedupe karta hai? TransactionID dono Passports mein unique hai ya register wise repeat ho sakta hai?

**Abhi khula, hissa (c): Modisoft delete ki race.** Service file aane pe pehle lock check karti hai (35 second tak wait) phir copy. Agar Modisoft is dauran file delete kar de to `FileMonitorService.cs:264` "no longer exists" log kar ke chhod deta hai, aur yeh transaction kabhi upload nahi hoti kyunke file gayab hai, restart scan bhi nahi dekhega. Sawal: Modisoft file aane ke kitni der baad delete karta hai? Seconds, minutes, ya din ke end pe?

### 4. Watcher pehli baar fail ho to service hamesha phansi — PENDING

**Masla:** `FileMonitorService.cs:85` `SetupFileWatcher()` exception khaa ke sirf log karta hai, return value nahi. Phir line 95 ka loop sirf uploads retry karta hai, watcher dobara nahi banata. Recovery sirf `OnWatcherError` pe hai, jo bina watcher ke aa hi nahi sakta. Matlab agar network share us ek lamhe mein gayab tha jab watcher ban raha tha, service restart tak koi file nahi uthegi.

**Fix idea:** `SetupFileWatcher` bool return kare. Fail pe inner loop break kar ke 30 second baad dobara path check aur watcher.

### 5. Auto-login token verify nahi karta — PENDING

**Masla:** `LdPosService/LoginForm.cs:75` DB mein user hai to seedha Dashboard, server se kuch nahi poochta. Token expire ho gaya to service ki uploads 401 pe chup chaap fail, Dashboard pe kuch nahi dikhta.

**Fix idea:** Dashboard load pe ek halka API call (ya logout endpoint nahi, koi "me" jaisa endpoint agar hai). Fail pe local user delete aur login screen.

### 6. DeptId 0 ka risk — PENDING

**Masla:** `LoginForm.cs:42` `pos_dept_id` seedha DeptId mein. API na bheje to 0. Phir `FileMonitorService.cs:698` koi MerchandiseCode 0 se match nahi karega, saari files delete.

**Fix idea:** Login response mein `pos_dept_id` 0 ya missing ho to login reject ya saaf warning.

### 7. Network-error break wala code dead tha — DONE (point 1 ke saath)

**Masla:** `ApiLibrary/ApiServices.cs` ka `UploadJsonAsync` sab exceptions khud kha ke false deta hai. To upload loop ke network catch blocks kabhi nahi chalte the, aur net down pe har pending transaction ek ek 30 second timeout khaati thi.

**Kya badla:** `uploadSuccess == false` pe break. Dead catch blocks hata diye. Commit 04597f3.

### 8. Watcher sirf Created event sunta hai — PENDING

**Masla:** `FileMonitorService.cs:219` sirf `Created` aur `Error`. Passport agar temp naam se likh ke `.xml` pe rename kare to file miss. Aur line 483 pe XML parse fail ho to temp delete, retry nahi, original folder mein pada rehta hai agle restart tak.

**Fix idea:** `Renamed` event bhi sunna. Parse fail pe ek baar 1-2 second baad dobara copy aur parse try karna (partial write ka case).

### 9. Service mein Console.WriteLine — PENDING

**Masla:** `ApiServices.cs` lines 105, 125, 130, 135, 140, 145 aur `DatabaseServices.cs` lines 46, 50, 54 `Console.WriteLine` use karti hain. Windows service mein console nahi, yeh logs gayab. Upload fail kyun hui, pata hi nahi chalta.

**Fix idea:** ApiLibrary mein `Microsoft.Extensions.Logging.Abstractions` ka `ILogger` inject karna, ya jaise `XmlJsonConverter` karta hai, `Action<string>` log callbacks.

### 10. Login JSON haath se jora hua — PENDING

**Masla:** `ApiServices.cs:36` string interpolation se JSON. UUID mein quote ya backslash aaye to request toot jaye.

**Fix:** `JsonConvert.SerializeObject(new { uuid })`. Ek line.

### 11. Retry comment aur code alag — PENDING

**Masla:** `FileMonitorService.cs:250` code 50 baar 700ms = 35 second. Comment aur log message 10 baar 500ms bolte hain.

**Faisla chahiye:** Kaunsa sahi hai? Phir doosra match karo.

### 12. Nested form chain — PENDING

**Masla:** `LoginForm.cs:50` aur `DashboardForm.cs:43` dono `ShowDialog` nested chalate hain. Login, Dashboard, phir naya Login, naya Dashboard. Har logout/login pe ek hidden form stack pe baitha rehta hai jab tak app band na ho.

**Fix idea:** Ek main form jo panels switch kare, ya logout pe Dashboard close kar ke `Application.Restart()`.

### 13. UI freeze, WaitForStatus UI thread pe — PENDING

**Masla:** `DashboardForm.cs:113` se 143 tak `WaitForStatus` UI thread pe, har ek 10 second tak. Browse Folder ke baad app 10 se 20 second jam.

**Fix idea:** Service control `Task.Run` mein, button disable, await.

### 14. UNC path pe sync Directory.Exists — PENDING

**Masla:** `FileMonitorService.cs:772` `Directory.Exists` network path pe sync hai. Network down ho to yeh call kaafi der latak sakti hai. Chhota masla.

### 15. Dead code aur faltu saaman — PENDING

- `Dapper` package `ApiLibrary.csproj` mein, kahin use nahi
- `DatabaseServices`: `GetUserByUUID`, `GetUserFolderPath`, `GetDatabasePath` koi call nahi karta
- `Transactions.IsProcessed` column hamesha 0, successful rows delete hoti hain
- `DashboardForm._storeId` use nahi hota
- `FileUtilities.DeleteFile` ka `logWarning` param use nahi hota
- Dono `.resx` mein same 179KB icon base64 embedded
- `Serilog.AspNetCore` jahan `Serilog.Extensions.Hosting` kaafi tha
- Do alag ProgramData folders: publish `LotteryDisplayPOS`, DB/logs `LdPosService`

## Background facts

- Store mein 2 Gilbarco Passport terminals. Ek waqt mein max 2 XML files.
- Backend `lotteryscreen.app` (production). Feb 2026 wala version `stage.lotteryscreen.app` pe tha.
- Git: 2026-10-06 ko purana repo `POS-DesktopApp-C-` delete kar ke naya `LD-GilbarcoFileProcessor` banaya. `LdPosService` folder hi repo hai. `..\POS-DesktopApp-C` clone stale hai, wahan se push mat karo.
- Publish: `FolderProfile.pubxml` → `C:\ProgramData\LotteryDisplayPOS\LdFileProcessor`, self-contained single-file win-x64. Yeh file gitignore mein hai (`*.pubxml`).
- Runtime data: `C:\ProgramData\LdPosService\` mein `PosData.db`, `logs\service-YYYYMMDD.log`, `TempFiles\`.

## Commits

| Commit | Date | Kya |
|---|---|---|
| 9b51aeb | 2026-10-06 | Initial commit, March 2026 production code |
| 04597f3 | 2026-10-06 | Point 1 (+7): periodic upload retry, semaphore lock, break on first failure |
