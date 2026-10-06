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
| 3 | POS folder ki originals kabhi delete nahi, restart pe sab dobara process | Bara | CHOR DO (dekho point 16) | - |
| 4 | Watcher pehli baar fail ho to service hamesha phansi | Bara | DONE | 27b1499 |
| 5 | Auto-login token verify nahi karta | Bara | CHOR DO (token kabhi expire nahi hota) | - |
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
| 16 | Server ka reject (4xx) aur network fail ek jaise treat; point 1 ke baad rejected row queue block kar sakti hai | Bara | CHOR DO (server 200 deta hai) | - |
| 17 | LdOposService aur LdFileProcessor same log folder aur same file naam | Chhota | CHOR DO | - |
| 18 | Mapped drive (Z:) service ko dikhta hi nahi, Dashboard usko accept kar leta hai | Bara | PENDING | |

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

**Caveat:** "pehli fail pe break" tab galat hota jab server kisi row ko hamesha error status se reject karta. Point 16 mein confirm hua ke server duplicate pe 200 deta hai, to yeh caveat lagu nahi hoti.

**Deploy:** Sirf code mein hai. Store machine pe service dobara publish aur restart zaroori.

### 2. Logout pe saari pending transactions delete — CHOR DO

**Masla:** `LdPosService/DashboardForm.cs:39` logout pe `DeleteAllTransactions()`. Offline logout ho to jo transactions abhi upload nahi hui thin woh bhi ud jaati hain.

**Faisla (Moiez, 2026-10-06):** Yeh intended hai. Logout matlab us user ka local data saaf. Aise hi rehne do.

### 3. POS folder ki originals kabhi delete nahi, restart pe sab dobara process — PENDING

**Masla:** Service original XML ko POS folder se kabhi delete nahi karti. `FileMonitorService.cs:274` sirf temp mein copy karta hai, aur delete sirf temp file ki hoti hai. Service restart ya network wapas aane pe line 88 aur 347 se `CopyExistingFilesAsync` folder ki har XML dobara copy (line 393) aur process karti hai. Pehle se uploaded wali bhi, kyunke unki DB row upload ke baad delete ho chuki hoti hai aur `INSERT OR IGNORE` unhe nayi row samajh ke dobara daal deta hai. Nateeja: duplicate upload.

**Faisla (Moiez, 2026-10-06), delete wala hissa: CHOR DO.** Usi folder se ek aur software, Modisoft, bhi padhta hai aur wohi files delete karta hai. Hum delete karein to Modisoft padh nahi payega. Passport bhi har 7 din mein folder khud khali kar deta hai. To originals ko haath nahi lagana, yeh intended hai.

**Hissa (b), restart pe duplicate upload: CHOR DO (Moiez, 2026-10-06).** Service restart pe duplicates bhejti hai, yeh Moiez ko pata hai. Server `check-json` apne paas log rakhta hai aur same TransactionID dobara aaye to rok deta hai. To duplicate bhejna masla nahi. Is se ek alag baat nikli, dekho point 16: server duplicate ko HTTP 200 se rokta hai ya error status se, is pe depend karta hai ke point 1 ka "pehli fail pe break" us row pe phans to nahi jayega.

**Hissa (c), Modisoft delete ki race: CHOR DO, evidence ki bunyad pe.** Modisoft files delete karta hai, lekin foran nahi: agar foran karta to restart pe folder khali milta aur duplicates kabhi na jaate. Duplicates jaate hain, matlab files ghanton tak padi rehti hain. To lock-wait ke dauran file gayab hone ka chance sirf theoretical hai. Nishani: agar kabhi service log mein "File no longer exists before copy" dikhe to yahi race hai, tab dobara dekhenge.

### 4. Watcher pehli baar fail ho to service hamesha phansi — DONE (27b1499)

**Faisla (Moiez, 2026-10-06):** "Kuch bhi ho baar baar retry pe jaye, service break nahi honi chahiye." Fix step 5 ke saath kiya.

**Kya badla (`LdFileProcessor/FileMonitorService.cs`):**
- Line 225 `TrySetupFileWatcher()`: bool deta hai, fail pe `_watcher = null`, kabhi throw nahi karta. Watcher pehle `_watcher` mein rakha jaata hai phir `EnableRaisingEvents = true` (wahi line jo share pe toot-ti hai), taake foran aane wala Error event sahi instance dekhe.
- Line 106: start pe watcher na bane to 30 second (`WatcherRetryDelay`, line 50) ruk ke `continue`, path dobara padho, dobara try. Infinite sleep khatam.
- Line 382 `OnWatcherError`: sirf tootay hue watcher ko null aur dispose karta hai. Purana alag 30-second `Task.Run` retry loop aur `_watcherRestartCts` hata diye.
- Line 296 `EnsureWatcherAliveAsync`: har minute wale loop se chalta hai. `_watcher == null` aur path accessible ho to watcher dobara banao, phir folder scan taake outage ki files uth jayein.
- Line 421 `ScanFolderIfDueAsync` + line 445 `ScanFolderAsync(reason)`: step 5. Har 5 minute (`FolderScanInterval`, line 54) folder scan. Is run mein pehle se handle ki hui files (naam se, in-memory `_handledFiles`) skip, 10 second (`ScanSettleTime`, line 58) se taza files agle scan ke liye chhod do. Restart pe list khali, sab dobara process, pehle jaisa. `CopyExistingFilesAsync` isi mein merge ho gaya (startup, watcher recovery, periodic teeno isko call karte hain).
- `_watcherLock`: watcher banane/dispose karne pe lock, kyunke loop thread aur watcher ka event thread dono haath lagate hain. `StopAsync` aur `Dispose` ab `DisposeWatcher()` use karte hain.

**Ab behaviour:** watcher start pe na bane to har 30 second try. Beech mein mare (Error event) to max 1 minute mein wapas. Bina Error event ke chup chaap mare to max 5 minute mein scan files utha leta hai. Koi bhi exception loop ko nahi todti, sab methods apne andar catch karte hain. Service sirf tab rukti hai jab Moiez khud stop kare.

**Log mein kya dikhega:** "File watcher could not be started. Retrying in 00:00:30", "File watcher is down and folder path is not accessible" (har minute jab tak path wapas na aaye), "Folder path is accessible again ... Restarting file watcher", "FOLDER SCAN (startup|watcher recovery|periodic) START". Periodic scan ko kuch na mile to chup.

**Deploy:** service dobara publish aur restart zaroori. Saath mein `sc failure` wala recovery set kar lo (neeche).

**Pehle ka analysis aur plan, reference ke liye:**

**Masla:** `FileMonitorService.cs:85` `SetupFileWatcher()` exception khaa ke sirf log karta hai, return value nahi. Phir line 95 ka loop sirf uploads retry karta hai, watcher dobara nahi banata. Recovery sirf `OnWatcherError` pe hai, jo bina watcher ke aa hi nahi sakta. Matlab agar network share us ek lamhe mein gayab tha jab watcher ban raha tha, service restart tak koi file nahi uthegi.

**Yehi masla `OnWatcherError` mein bhi hai:** line 343 `SetupFileWatcher()` call ke baad seedha `return` hai, "success" samajh ke. Agar setup andar fail hua to recovery loop khatam, watcher mara hua, koi nahi dekhta.

**Suggested fix (2026-10-06), ek hi recovery raasta:**
1. `SetupFileWatcher` ko `TrySetupFileWatcher()` banao jo bool de. Fail pe aadha bana watcher dispose, `_watcher = null`.
2. `OnWatcherError` mein watcher dispose, `_watcher = null`, log (Win32 error 64 ko warning rakhne wala hissa rehne do). Uska 30-second `Task.Run` retry loop aur `_watcherRestartCts` hata do.
3. `ExecuteAsync`: path milne ke baad `TrySetupFileWatcher()` false de to 30 second ruk ke `continue`, matlab path dobara padho aur dobara try.
4. Har minute wale loop mein ek check: `_watcher == null` aur path accessible ho to `TrySetupFileWatcher()`, success pe `CopyExistingFilesAsync()` taake outage ki files uth jayein. Uske baad pending uploads jaise abhi hai.

Natija: start pe fail ho ya baad mein mare, dono case zyada se zyada 1 minute mein recover. Do alag recovery mechanisms ki jagah ek.

**Confirmed (Moiez, 2026-10-06):** folder UNC hai, `\\10.5.48.2\XMLGateway\BOOutBox` (Passport ka XMLGateway BOOutBox), kabhi `Z:` mapped drive se bhi. Wi-Fi pe "network path not accessible" aur kabhi Win32 error aata hai. **Requirement:** service kuch bhi ho jaye retry karti rahe, jab tak Moiez khud stop na kare.

**Optional step 5, silent death ke liye:** FileSystemWatcher network share pe kabhi bina `Error` event ke bhi mar jaata hai (Passport reboot, SMB session stale). Tab `_watcher` null nahi hota, step 4 usko nahi pakdega. Safety net: har 5 minute folder ka ek scan, sirf woh files process jo is run mein pehle nahi dekhi (in-memory list, restart pe khali). Yeh server dedupe nahi hai, bas ek run ke andar same file dobara na uthe. Point 8 ka rename wala case bhi isi se cover ho jaata hai.

**Deployment tip (code nahi):** process khud crash ho jaye to Windows usko wapas chalaye: `sc failure LdFileProcessor reset= 86400 actions= restart/60000/restart/60000/restart/60000`. Ya services.msc mein Recovery tab, teeno pe "Restart the Service".

### 5. Auto-login token verify nahi karta — PENDING

**Masla:** Yeh WinForms app ki baat hai, lekin asar service pe padta hai. `LdPosService/LoginForm.cs:75` DB mein user row hai to seedha Dashboard, server se kuch nahi poochta. Service khud kabhi login nahi karti, woh bas DB ka saved `AccessToken` utha ke uploads mein lagati hai. Agar woh token server pe expire ya revoke ho jaye to service ki har upload 401 pe fail, row DB mein, har minute retry, aur Dashboard pe sirf "Welcome" dikhta hai, koi warning nahi.

**Ek rukawat:** app typed UUID save nahi karta, DB ka `UUID` column asal mein server ka `user.id` hai. To app chup chaap dobara login bhi nahi kar sakta.

**Faisla (Moiez, 2026-10-06): CHOR DO.** lotteryscreen.app ka token kabhi expire ya revoke nahi hota, sirf logout pe khatam hota hai. To saved token hamesha valid hai, verify karne ki zaroorat nahi.

**Agar kabhi yeh badle** (token expiry ya server-side revoke aaye): typed UUID bhi DB mein save karo, app start pe usi se dobara login kar ke taza token lo, fail pe login screen.

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

### 16. Server ka reject aur network fail ek jaise treat hote hain — PENDING

**Masla:** `ApiLibrary/ApiServices.cs` ka `UploadJsonAsync` sirf `IsSuccessStatusCode` dekhta hai aur bool deta hai, response body padhta hi nahi. Server agar duplicate ya galat data ko error status (400, 409, 422) se rokta hai to client usko network failure jaisa samajhta hai: row DB mein rehti hai aur baar baar retry hoti hai. Point 1 ke baad yeh zyada bura hai: upload pass pehli fail pe break hota hai, to agar list mein sab se pehli row server-rejected hai, uske peeche ki saari nayi transactions har minute block hongi jab tak logout na ho.

**Agar server duplicate pe HTTP 200 deta hai** (body mein success 0 ya koi message): koi masla nahi, `IsSuccessStatusCode` true, row delete ho jaati hai. Tab yeh point CHOR DO.

**Fix idea (dono case mein behtar):** `UploadJsonAsync` bool ki jagah teen outcome de: Success, Rejected (4xx, server ne samajh ke mana kiya), Failed (network, timeout, 5xx). Rejected pe status aur body log karo, row hatao, agli row pe chalo. Failed pe break aur agle minute retry. Response body log hone se duplicate ka message bhi log mein dikhega, abhi woh kahin nahi dikhta.

**Moiez ka faisla (2026-10-06):** Windows service ko duplicate rokne ki zaroorat nahi, server rokta hai. Client-side dedupe (IsProcessed retention, FileName check waghera) nahi karna. Point 16 dedupe ke baare mein nahi hai, sirf is baare mein hai ke server ka "mana" client ke liye "network fail" na ban jaye.

**Faisla (Moiez, 2026-10-06): CHOR DO.** `check-json` duplicate pe HTTP 200 deta hai. To `IsSuccessStatusCode` true, row delete, queue block nahi hoti. Point 1 ka "pehli fail pe break" sirf asli network/server failures pe lagta hai, jo sahi hai.

### 17. LdOposService aur LdFileProcessor same log folder aur same file naam — PENDING

**Masla:** Dono services `C:\ProgramData\LdPosService\logs\service-YYYYMMDD.log` likhti hain (`LdFileProcessor/Program.cs:16`). Serilog ka file sink by default file exclusively kholta hai. Agar dono ek hi machine pe saath chalein to jo pehle shuru hui usi ke logs likhe jayenge, doosri ke chup chaap gayab. Dev machine pe yehi dikha: July 2026 ke logs sirf Opos ke the, FileProcessor ka ek bhi nahi.

**Faisla (Moiez, 2026-10-06): CHOR DO.** Opos aur FileProcessor kabhi ek machine pe nahi chalenge. Jis customer ke paas Opos hai woh ticket scanner se update karta hai, usko FileProcessor ki zaroorat hi nahi.

### 18. Mapped drive (Z:) service ko dikhta hi nahi — PENDING

**Masla:** Mapped drive letter user ke logon session ki cheez hai. Windows service session 0 mein LocalSystem pe chalti hai, usko user ka `Z:` nazar hi nahi aata. Dashboard mein agar `Z:\XMLGateway\BOOutBox` chuna jaye to `DashboardForm.cs:72` `Directory.Exists` user ke liye true dega, path DB mein save ho jayega, lekin service ke liye `Directory.Exists` hamesha false: log mein "Folder path found but not accessible" har 30 second, hamesha. Koi file kabhi nahi uthegi aur user ko lagega network ka masla hai.

**Fix idea:** Dashboard mein folder chunte waqt agar path drive letter se shuru ho aur woh network drive ho, to `WNetGetConnection` se UNC nikaal ke wohi save karo (`Z:\...` → `\\10.5.48.2\XMLGateway\...`). Local drive ho to jaise hai. Fallback: convert na ho sake to saaf message "network drive letter nahi, `\\server\share` path chuno".

## Background facts

- Store mein 2 Gilbarco Passport terminals. Ek waqt mein max 2 XML files.
- Backend `lotteryscreen.app` (production). Feb 2026 wala version `stage.lotteryscreen.app` pe tha.
- Git: 2026-10-06 ko purana repo `POS-DesktopApp-C-` delete kar ke naya `LD-GilbarcoFileProcessor` banaya. `LdPosService` folder hi repo hai. `..\POS-DesktopApp-C` clone stale hai, wahan se push mat karo.
- Publish: `FolderProfile.pubxml` → `C:\ProgramData\LotteryDisplayPOS\LdFileProcessor`, self-contained single-file win-x64. Yeh file gitignore mein hai (`*.pubxml`).
- Runtime data: `C:\ProgramData\LdPosService\` mein `PosData.db`, `logs\service-YYYYMMDD.log`, `TempFiles\`.
- Dev machine pe `C:\ProgramData\LdPosService\logs\` ke July 2026 logs ek doosri service ke hain (LdOposService: Verifone auth, CoreScanner barcode). Woh bhi same folder aur same `service-.log` naam use karti hai. Agar store machine pe dono services saath chalein to Serilog ka file sink ek waqt mein ek process ko hi file deta hai, doosri ke logs chup chaap gayab honge. Sawal: dono ek machine pe chalti hain?

## Commits (sirf code; notes ke commits yahan nahi)

| Commit | Date | Kya |
|---|---|---|
| 9b51aeb | 2026-10-06 | Initial commit, March 2026 production code |
| 04597f3 | 2026-10-06 | Point 1 (+7): periodic upload retry, semaphore lock, break on first failure |
| 27b1499 | 2026-10-06 | Point 4: TrySetupFileWatcher, keep-alive loop recreates watcher, 5-minute folder scan safety net |
