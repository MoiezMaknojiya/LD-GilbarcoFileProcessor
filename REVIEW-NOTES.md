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
| 4 | Watcher pehli baar fail ho to service hamesha phansi | Bara | DONE | 27b1499, e6e2af5 |
| 5 | Auto-login token verify nahi karta | Bara | CHOR DO (token kabhi expire nahi hota) | - |
| 6 | DeptId 0 ka risk | Bara | DONE | 66192af |
| 7 | Network-error break wala code dead tha | Darmiyana | DONE (point 1 ke saath) | 04597f3 |
| 8 | Watcher sirf Created event sunta hai | Darmiyana | CHOR DO (Gilbarco sirf Created karta hai) | - |
| 9 | Service mein Console.WriteLine | Darmiyana | DONE | 082929d |
| 10 | Login JSON haath se jora hua | Darmiyana | DONE (9 ke saath) | 082929d |
| 11 | Retry comment aur code alag | Darmiyana | DONE | 629989f |
| 12 | Nested form chain | Chhota | PENDING | |
| 13 | UI freeze, WaitForStatus UI thread pe | Chhota | PENDING | |
| 14 | UNC path pe sync Directory.Exists | **Bara** (client logs: 12-16 minute block) | DONE | 7190a87 |
| 15 | Dead code aur faltu saaman | Chhota | DONE | 6e49536 |
| 16 | Server ka reject (4xx) aur network fail ek jaise treat; point 1 ke baad rejected row queue block kar sakti hai | Bara | CHOR DO (server 200 deta hai) | - |
| 17 | LdOposService aur LdFileProcessor same log folder aur same file naam | Chhota | CHOR DO | - |
| 18 | Mapped drive (Z:) service ko dikhta hi nahi, Dashboard usko accept kar leta hai | Bara | DONE | 96bdd78 |
| 19 | Disable-RunExeAsAdmin.bat poori machine ka UAC prompt band karta hai | Darmiyana (deployment) | DONE | d58099c |

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

**Client logs ne confirm kiya (1-3 Oct):** 1 Oct 21:27 transaction 4731 ki upload fail (14ms mein, matlab network nahi tha, Wi-Fi gir raha tha), 23:36 ko 4731, 4742, 4752, 4760 phir fail. Chaaron 2 Oct 17:31 pe upload huin, jab service manually restart hui. **20 ghante ki delay.** 2 Oct 22:40 ko 5121 aur 5130 fail, 3-3 baar, 3 Oct ke log mein kabhi upload nahi huin (service deaf thi). Naye code mein yeh har minute retry hoti, net wapas aate hi chali jaati.

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

**Deploy:** service dobara publish aur restart zaroori. Crash recovery Install .bat mein hai (neeche "Deployment" dekho), manual command nahi.

**Refinement DONE (e6e2af5):** watcher bina Error event ke chup chaap mar jaye to pehle service 5-minute polling mode mein chalti rehti thi, watcher restart tak wapas nahi aata tha. Ab `ScanFolderAsync` (line 491) mein: periodic scan ko aisi files milein jo is run mein handle nahi hui, to watcher ne unko report nahi kiya, matlab mara hua hai, wahin `TrySetupFileWatcher()` se dobara banao. Sehatmand watcher galti se dobara ban jaye to koi nuqsan nahi. Ab teeno case (start pe fail, Error event, chup chaap maut) max 5 minute mein poori tarah recover.

**Client logs ne confirm kiya (2026-10-08, logs 1-3 Oct):** purane build pe yeh 3 din mein 2 baar hua, dono baar poora din deaf:
- 2 Oct 06:03:14 "Network path is accessible again (attempt 7). Restarting file watcher." → 06:05:44 `Error setting up file watcher. System.IO.FileNotFoundException: Error reading the \\10.5.48.2\XMLGateway\BOOutBox directory. at FileSystemWatcher.StartRaisingEvents()` → "Folder not found" → recovery loop khatam. Uske baad 17:30 tak log mein ek line nahi, ek file nahi. 17:30 pe kisi ne service manually restart ki, startup scan ko "Found 0 existing XML files" mila (Modisoft din ki files delete kar chuka tha), phir 30 minute mein 20 nayi files. Matlab 2 Oct ka poora din (06:05 se 17:30) hamari service behri thi aur us din ki lottery sales lotteryscreen.app tak kabhi nahi pohnchi.
- 3 Oct 04:42:41 "accessible again (attempt 1)" → 04:45:23 "Folder path is not accessible" (SetupFileWatcher ka andar wala check 2m42s latka, point 14) → "Folder not found" → recovery loop khatam. Log wahin khatam, poore din ek line nahi. 3 Oct bhi deaf.
Exception ka text wahi hai jo analysis mein likha tha: `EnableRaisingEvents = true` (purane code ki line 212) share reconnect ke beech throw karta hai.

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

**Deployment (code nahi), crash recovery:** client pe koi manual command nahi chalti, service `Install-WindowsService.bat` se lagti hai jo client admin ke tor pe chalata hai (Opos wali `C:\ProgramData\LotteryDisplayOPOS\Install-WindowsService.bat` isi pattern pe hai, usme line 77 pe `sc failure` pehle se hai). FileProcessor ki .bat mein `sc create` ke baad yeh do lines honi chahiye:

```
sc failure LdFileProcessor reset= 86400 actions= restart/5000/restart/10000/restart/30000
sc failureflag LdFileProcessor 1
```

Pehli line: process crash ho to Windows 5s, 10s, 30s baad wapas chalaye, 24 ghante baad counter reset. Doosri: agar service crash ke bagair non-zero exit code se band ho (host ka unhandled error) to bhi wahi recovery lage. Yeh sirf us case ke liye hai jo code ke bahar hai (runtime/native crash); code ke andar ab koi raasta nahi jahan se process khud mare.

**Check kiya (2026-10-06):** FileProcessor ki asli .bat `E:\Dropbox\LD shared\Debug App\POS-Gilbarco-CSharp\LotteryDisplayPOS\Install-WindowService.bat` hai, 2026-02-02 ki. Usme `sc create` (line 46) aur `sc config obj= "NT AUTHORITY\NetworkService"` (line 56) hai, lekin **`sc failure` nahi hai**. Crash recovery abhi client pe set nahi hoti. Opos wali .bat (July) mein hai, yeh us se purani hai.

**DONE (2026-10-06, 71ef215):** Dropbox folder mein nayi file `updated-Install-WindowService.bat` banai (purani ko haath nahi lagaya, Moiez ne kaha naam ke shuru mein "updated" lagao). Farq sirf naya step `[5/5]` jo `sc failure` aur `sc failureflag 1` chalata hai, header comment, aur SUCCESS message. Baaki original jaisi, NetworkService account aur "service start nahi hoti" wahi. Repo mein `deploy\` folder bana: `Install-WindowService.bat` (updated wali, saaf naam se), `Unistall-WindowService.bat`, `Disable-RunExeAsAdmin.Bat`, `Installation Guide.txt` jaise shipped hain. **Client ke liye:** agli install pe `updated-` wali chalani hai, purani delete kar do ya Dropbox mein purani ki jagah updated ka naam rakh do.

**Service account NetworkService hai, LocalSystem nahi.** Matlab: (a) mapped drive Z: isko bhi nahi dikhta, point 18 wahi rehta hai; (b) UNC share pe access NetworkService ki identity se hota hai, production mein chal raha hai to share isko allow karta hai; (c) `InitializeDatabase` ka ACL set karna service ke andar fail hoga (admin chahiye), lekin Dashboard admin se pehle chal ke folder aur ACL bana deta hai aur NetworkService BUILTIN\Users mein hai, to DB aur logs likhna chalta hai.

### 5. Auto-login token verify nahi karta — PENDING

**Masla:** Yeh WinForms app ki baat hai, lekin asar service pe padta hai. `LdPosService/LoginForm.cs:75` DB mein user row hai to seedha Dashboard, server se kuch nahi poochta. Service khud kabhi login nahi karti, woh bas DB ka saved `AccessToken` utha ke uploads mein lagati hai. Agar woh token server pe expire ya revoke ho jaye to service ki har upload 401 pe fail, row DB mein, har minute retry, aur Dashboard pe sirf "Welcome" dikhta hai, koi warning nahi.

**Ek rukawat:** app typed UUID save nahi karta, DB ka `UUID` column asal mein server ka `user.id` hai. To app chup chaap dobara login bhi nahi kar sakta.

**Faisla (Moiez, 2026-10-06): CHOR DO.** lotteryscreen.app ka token kabhi expire ya revoke nahi hota, sirf logout pe khatam hota hai. To saved token hamesha valid hai, verify karne ki zaroorat nahi.

**Agar kabhi yeh badle** (token expiry ya server-side revoke aaye): typed UUID bhi DB mein save karo, app start pe usi se dobara login kar ke taza token lo, fail pe login screen.

### 6. DeptId 0 ka risk — PENDING

**Masla:** `LoginForm.cs:42` `pos_dept_id` seedha DeptId mein. API na bheje to 0. Phir `FileMonitorService.cs:698` koi MerchandiseCode 0 se match nahi karega, saari files delete.

**Fix idea:** Login response mein `pos_dept_id` 0 ya missing ho to login reject ya saaf warning.

**Context (2026-10-06):** Installation Guide ka PRE REQUISITE yehi hai: Lottery Display App ke Store Settings mein "Pos Lottery Dept ID" aur "Pos Payout Dept ID" bharna. Koi bhool jaye to exactly yeh case banta hai, DeptId 0, saari files chup chaap delete. Fix wala warning us bhool ko install ke waqt hi pakad lega.

**Faisla (Moiez, 2026-10-08):** Dashboard na khule, error dikhe, OK pe logout API call ho taake server pe token jama na hon. Dept kabhi 0 nahi hota.

**DONE (2026-10-08, commit 66192af):**
- `LdPosService/LoginForm.cs`: login success lekin `pos_dept_id` 0 ya missing → "Store Not Configured" message jo batata hai kahan set karna hai (Store Settings → Pos Lottery Dept ID aur Pos Payout Dept ID, phir dobara login aur BOOutBox folder), OK ke baad `LogoutAsync` usi token ke saath, user save nahi, login screen pe wapas. Auto-login pe bhi: purane build se saved user jiska DeptId 0 ho → wahi message, token release, saved user aur transactions delete, login screen.
- `LdPosService/DashboardForm.cs`: label ab "Welcome, X!   Store 430, Lottery Dept 2". Constructor mein storeId aur deptId wapas aaye (point 15 mein unused the, ab use hain).
- `LdFileProcessor/FileMonitorService.cs`: user load pe DeptId 0 ho to ERROR line fix ki hidayat ke saath (line 224), aur har file pe Info ki jagah ERROR "Ignoring ...: the saved login has no POS lottery department" (line 685). Files pehle ki tarah process nahi hotin, temp copy delete.
- Service ko DeptId 0 wale user se chhutkara tab milta hai jab user dobara login kar ke Browse Folder dabaye (service restart). Tab tak log ERROR deta rahega, chup nahi.

### 7. Network-error break wala code dead tha — DONE (point 1 ke saath)

**Masla:** `ApiLibrary/ApiServices.cs` ka `UploadJsonAsync` sab exceptions khud kha ke false deta hai. To upload loop ke network catch blocks kabhi nahi chalte the, aur net down pe har pending transaction ek ek 30 second timeout khaati thi.

**Kya badla:** `uploadSuccess == false` pe break. Dead catch blocks hata diye. Commit 04597f3.

### 8. Watcher sirf Created event sunta hai — PENDING

**Masla:** `FileMonitorService.cs:219` sirf `Created` aur `Error`. Passport agar temp naam se likh ke `.xml` pe rename kare to file miss. Aur line 483 pe XML parse fail ho to temp delete, retry nahi, original folder mein pada rehta hai agle restart tak.

**Fix idea:** `Renamed` event bhi sunna. Parse fail pe ek baar 1-2 second baad dobara copy aur parse try karna (partial write ka case).

**Faisla (Moiez, 2026-10-07): CHOR DO.** Gilbarco Passport BOOutBox mein file seedha create karta hai, rename nahi, to `Created` kaafi hai. Partial write ka risk pehle se lock-check (35 second) se aur ab scan ke 10-second settle time se cover hai.

### 9. Service mein Console.WriteLine — PENDING

**Masla:** `ApiServices.cs` lines 105, 125, 130, 135, 140, 145 aur `DatabaseServices.cs` lines 46, 50, 54 `Console.WriteLine` use karti hain. Windows service mein console nahi, yeh logs gayab. Upload fail kyun hui, pata hi nahi chalta.

**Fix idea:** ApiLibrary mein `Microsoft.Extensions.Logging.Abstractions` ka `ILogger` inject karna, ya jaise `XmlJsonConverter` karta hai, `Action<string>` log callbacks.

**DONE (Moiez: "point 9 karo, dekh lena sab sahi se log ho", 2026-10-07, commit 082929d):**
- `ApiLibrary` mein `Microsoft.Extensions.Logging.Abstractions` 10.0.2 (wahi version jo worker ke Hosting 10.0.2 ke saath aata hai).
- `ApiServices.cs` poora dobara likha. Constructor `ILogger<ApiServices>?` leta hai: worker mein DI khud Serilog wala logger de deta hai (`AddSingleton<ApiServices>` pehle se tha, kuch badalna nahi pada), WinForms app `new ApiServices()` chalati rehti hai, wahan NullLogger, kyunke app MessageBox dikhati hai.
- Har fail ab wajah ke saath log hoti hai: HTTP status, reason aur response body (500 chars tak), ya timeout, ya exception. Pehle upload fail pe server ka jawab padha hi nahi jaata tha, log mein sirf "Failed to upload transaction N" aata tha. Ab "Upload rejected: HTTP 422 Unprocessable Content. Response: {...}" jaisa aayega, ya "Upload failed: no response within 00:00:30", ya "Upload failed: network error: No such host is known".
- Login success/fail aur logout success/fail bhi log hote hain (user id, store, dept). Token aur UUID kabhi log nahi hote. Upload success sirf Debug level pe (service khud per transaction "uploaded successfully" likhti hai, shor nahi chahiye).
- `DatabaseServices.InitializeDatabase(ILogger? logger = null)`: DB ka path aur folder permissions ka nateeja log mein (teen purani Console lines). `FileMonitorService` apna logger pass karta hai; WinForms app bina logger ke call karti hai, pehle jaisa.
- Database ke wrapped exceptions ab `InnerException` rakhte hain, to log mein asli SQLite error aur stack dikhta hai, sirf message nahi.
- Baaki jo pehle se theek tha woh waisa hi: `XmlJsonConverter` aur `FileUtilities` callbacks se FileMonitorService ke logger mein likhte hain, Serilog file `C:\ProgramData\LdPosService\logs\service-YYYYMMDD.log`, daily rolling, 30 din, Information level, `{Exception}` template mein shamil.

**Test:** build pass. Live run nahi kiya, kyunke is machine ki DB mein shayad asli user/token ho aur service production pe upload kar deti. Naye build ke pehle start pe log mein yeh lines dikhni chahiye: "Database file: C:\ProgramData\LdPosService\PosData.db", "Folder permissions set..." ya "Could not set folder permissions..." warning, phir "File watcher started for UNC network path".

### 10. Login JSON haath se jora hua — PENDING

**Masla:** `ApiServices.cs:36` string interpolation se JSON. UUID mein quote ya backslash aaye to request toot jaye.

**Fix:** `JsonConvert.SerializeObject(new { uuid })`. Ek line.

**DONE (2026-10-07, commit 082929d, point 9 ke saath):** `ApiServices.LoginAsync` dobara likhte waqt yahi ek line daal di, string jodna khatam. Alag se poochha nahi kyunke fix pehle se agreed tha aur usi method mein tha.

### 11. Retry comment aur code alag — PENDING

**Masla:** `FileMonitorService.cs:250` code 50 baar 700ms = 35 second. Comment aur log message 10 baar 500ms bolte hain.

**Faisla chahiye:** Kaunsa sahi hai? Phir doosra match karo.

**Scene samjhaya (2026-10-07):** Functional bug nahi hai, jhoot bolne wala comment aur log hai. Code asal mein 50 baar 700ms wait karta hai, matlab 35 second tak file ke free hone ka intezaar. Comment aur log message purane 10 x 500ms = 5 second wale version ke hain; kisi ne numbers badhaye aur text bhool gaya. Nateeja log mein aisa dikhta hai: "File is still in use, retrying in 500ms (11/10)", "(12/10)" ... "(50/10)", aur aakhir mein "still locked after 10 retries" jab asal mein 50 hue. Jo log padhe woh confuse hota hai, 500ms likha hai lekin 700ms ruk raha hai, 10 ki limit likhi hai lekin 50 tak gin raha hai.

**Recommendation:** 35 second theek hai (Passport likhne mein kam waqt leta hai, Modisoft bhi padhte waqt thodi der lock rakh sakta hai, 5 second kam the isi liye badhaye gaye honge). Fix: do constants `LockRetryCount = 50` aur `LockRetryDelay = 700ms`, loop aur dono log messages unhi se number lein, taake dobara kabhi drift na ho. Saath mein: ab agar 35 second baad bhi locked ho to file skip hoti hai lekin khoti nahi, point 4 ka 5-minute scan usko dobara uthata hai kyunke woh handled list mein nahi gayi. Faisla baaki: 35 second rakhna hai ya koi aur number?

**Faisla (Moiez, 2026-10-07): 30 second.** DONE, commit 629989f. `FileMonitorService.cs` line 63: `LockRetryCount = 60`, `LockRetryDelay = 500ms`, 60 x 500ms = 30 second. Loop (line 343) aur dono log messages ab inhi constants se number lete hain, "retrying in 500ms (7/60)" aur "still locked after 60 retries (30s). Skipping for now; the periodic folder scan will pick it up". Number badalna ho to sirf constants.

**Update (2026-10-08, point 14 ke saath, 7190a87):** 60 attempts ki jagah ab `LockWaitBudget = 30 second` ghadi se. Wajah client log: ek "30 second" wait 3.5 minute chala kyunke har `IsFileLocked` call network pe latki. Log ab "retrying in 500ms (12s of 30s)" aur "still locked after 30s" likhta hai.

### 12. Nested form chain — PENDING

**Masla:** `LoginForm.cs:50` aur `DashboardForm.cs:43` dono `ShowDialog` nested chalate hain. Login, Dashboard, phir naya Login, naya Dashboard. Har logout/login pe ek hidden form stack pe baitha rehta hai jab tak app band na ho.

**Fix idea:** Ek main form jo panels switch kare, ya logout pe Dashboard close kar ke `Application.Restart()`.

### 13. UI freeze, WaitForStatus UI thread pe — PENDING

**Masla:** `DashboardForm.cs:113` se 143 tak `WaitForStatus` UI thread pe, har ek 10 second tak. Browse Folder ke baad app 10 se 20 second jam.

**Fix idea:** Service control `Task.Run` mein, button disable, await.

### 14. UNC path pe sync Directory.Exists — PENDING

**Masla:** `FileMonitorService.cs:772` `Directory.Exists` network path pe sync hai. Network down ho to yeh call kaafi der latak sakti hai. Chhota masla.

**Client logs (1-3 Oct 2026) ne dikhaya ke yeh chhota nahi hai.** Purane code ka recovery loop "Retrying in 30 seconds" likhta hai, lekin attempt lines ke beech 12 se 16 minute ka farq hai (neeche "Client logs analysis" section). Matlab `Directory.Exists` share down hone pe har baar 12-16 minute block karta hai. Isi wajah se 3 Oct ko "accessible again" ke 2 minute 42 second baad "Folder path is not accessible" aaya: pehla check turant true, doosra check (SetupFileWatcher ke andar) 2m42s latka aur false. Naye code mein yeh aur zaroori hai: keep-alive loop har minute `IsPathAccessible` call karta hai, share down ho to har iteration 14 minute lat-kegi, aur usi loop mein pending uploads ka retry bhi hai, woh bhi 14 minute late ho jayega.

**Fix plan (2026-10-08):**
1. `IsPathAccessibleAsync`: `Directory.Exists` thread pool pe chalao, 10 second se zyada jawab na aaye to "abhi accessible nahi" maano. Ek waqt mein ek hi check in-flight rahe, pehla abhi latka ho to naya thread na banao, usi ka intezaar karo. Latki hui call apne aap khatam ho jaati hai.
2. Watcher banana (`new FileSystemWatcher` ka Path setter bhi `Directory.Exists` karta hai, aur `EnableRaisingEvents` directory handle kholta hai) bhi thread pool pe timeout ke saath, 30 second. Timeout pe fail maano, agle minute phir.
3. Folder scan mein `Directory.GetFiles` aur har `File.Copy` bhi timeout ke saath (20s aur 30s), taake scan loop ko na latkaye.
4. File-lock wait 30 second wall-clock budget pe, iteration count pe nahi: 1 Oct ko ek file ka lock-wait 3.5 minute chala kyunke har `IsFileLocked` call khud network pe latki.
Natija: keep-alive loop sach mein har minute chalega, outage mein bhi.

**DONE (2026-10-08, commit 7190a87), sab `LdFileProcessor/FileMonitorService.cs` mein:**
- `IsPathAccessibleAsync` (line 978): `Directory.Exists` thread pool pe, 10 second (`PathCheckTimeout`) mein jawab na aaye to "abhi accessible nahi". Ek waqt mein ek hi check in-flight (`_pathCheck`), latka hua ho to agla caller usi ka 10 second intezaar karta hai, naya thread nahi banata. Latki call khud khatam ho jaati hai.
- `TrySetupFileWatcherAsync` (line 249) + `CreateWatcher` (line 315): watcher ka constructor (uska Path setter bhi `Directory.Exists` karta hai) aur `EnableRaisingEvents` thread pool pe, 30 second (`WatcherCreateTimeout`). Timeout pe fail, agle minute phir; der se bana hua watcher dispose. Chhota race bhi band: watcher publish hone se pehle Error de de to `_deadWatchers` mein note, publish nahi hota.
- `ScanFolderAsync`: folder listing + filter ek task mein 20 second (`FolderListTimeout`), har `File.Copy` 30 second (`FileCopyTimeout`). Copy timeout pe file handled mark nahi hoti, agla scan dobara try karta hai.
- Lock wait: `LockRetryCount` gaya, ab `LockWaitBudget` 30 second ghadi se (Stopwatch), 500ms ke farq se check. Ek check khud latak jaye to bhi budget ke baad loop khatam.
- Helper `RunWithTimeoutAsync<T>` (line 1032): koi bhi blocking call thread pe + timeout, exception caller tak waise hi pohnchti hai.

**Retry timings ab (naya code):**

| Kya | Kitni der mein |
|---|---|
| Path check ka jawab | max 10 second, warna "not accessible" |
| Start pe path na mile ya accessible na ho | har 30 second dobara DB se path aur check |
| Start pe watcher na bane | 30 second ruk ke dobara |
| Keep-alive loop (watcher check + scan + uploads) | har 1 minute, ab sach mein 1 minute |
| Watcher gira (Error event) aur share wapas | agle minute tick pe dobara, max ~1 minute + 30 second create |
| Watcher chup chaap mara | max 5 minute (periodic scan files uthata hai aur watcher dobara banata hai) |
| Pending uploads | har 1 minute, pehli fail pe pass band, agle minute phir |
| Locked file | 30 second wait, phir skip, 5 minute ke andar scan dobara |
| Process crash | Windows 5s, 10s, 30s baad wapas (install .bat) |

### 15. Dead code aur faltu saaman — DONE (6e49536)

**Kya badla (2026-10-07):**
- `Dapper` package hata diya.
- `DatabaseServices`: `GetDatabasePath`, `GetUserByUUID`, `GetUserFolderPath` hata diye, aur `LoginForm_Load` ki commented debug line bhi.
- `Transactions.IsProcessed` column schema, insert, select aur count se nikaal diya. Purani DBs mein column reh jayega (default 0 hai, insert mein na ho to bhi chalta hai), nayi DBs bina column ke banengi. `Transaction` model se bhi property gayi.
- `FileUtilities.DeleteFile` ka `logWarning` param gaya, saat call sites update.
- Models ki 5 faltu `using` lines (template ki) hata di, ImplicitUsings pehle se on hai.
- `Serilog.AspNetCore` ki jagah `Serilog.Extensions.Hosting` 10.0.0, wahi jo `UseSerilog()` ke liye chahiye. ASP.NET Core ke packages ab nahi aate.
- Worker `Program.cs` ka commented template code gaya. `FileMonitorService` ka stale "internet check" comment theek.
- `DashboardForm` ka `_storeId` field aur constructor param gaya, `LoginForm` ke dono call sites update.
- Icon: dono forms ab exe se icon uthate hain (`Icon.ExtractAssociatedIcon(Application.ExecutablePath)`), constructor mein ek line. Dono `.resx` 3119 lines se 119 lines ke template pe wapas, `Designer.cs` se `resources` aur `Icon =` lines gayi. Build ke baad check: resx valid XML, BOM sab jagah preserved.

**Jaan ke nahi badla:** do alag ProgramData folders (publish `LotteryDisplayPOS`, DB/logs `LdPosService`). Data folder badalne se purani installs ki DB aur logs ka raasta toot jaata, aur publish folder install .bat mein hardcoded hai. Faida koi nahi, risk hai. Aise hi rehne do.

**Purani list, reference ke liye:**
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

### 18. Mapped drive (Z:) service ko dikhta hi nahi — DONE (96bdd78)

**Kya badla (2026-10-07), sirf `LdPosService/DashboardForm.cs`:**
- Naya helper `TryGetServicePath` (line 118 ke aas paas): path `\\` se shuru ho to jaise hai; drive letter ho aur `DriveInfo.DriveType` Network na ho (C:, D:) to jaise hai; Network ho to `WNetGetConnection` (mpr.dll) se drive letter ka share nikaal ke `\\server\share` + baaki path bana deta hai.
- Browse Folder (line 76): Directory.Exists ke baad, save se pehle, yeh helper chalta hai. DB mein hamesha UNC jaata hai. Success message mein UNC dikhta hai, aur agar user ne Z: chuna tha to ek note "Z:\... is a mapped drive, the service will use the network path shown above".
- Share pata na chal sake (drive disconnected, error code): save nahi hota, message "network drive letter nahi, `\\10.5.48.2\XMLGateway\BOOutBox` jaisa path chuno".

**Test nahi ho saka yahan:** dev machine pe koi mapped drive nahi. Store pe ek baar check karna: Z: map karo, Browse Folder mein `Z:\BOOutBox` chuno, success message mein `\\10.5.48.2\...` dikhna chahiye.

**Pehle ka text, reference ke liye:**

**Masla:** Mapped drive letter user ke logon session ki cheez hai. Windows service session 0 mein LocalSystem pe chalti hai, usko user ka `Z:` nazar hi nahi aata. Dashboard mein agar `Z:\XMLGateway\BOOutBox` chuna jaye to `DashboardForm.cs:72` `Directory.Exists` user ke liye true dega, path DB mein save ho jayega, lekin service ke liye `Directory.Exists` hamesha false: log mein "Folder path found but not accessible" har 30 second, hamesha. Koi file kabhi nahi uthegi aur user ko lagega network ka masla hai.

**Fix idea:** Dashboard mein folder chunte waqt agar path drive letter se shuru ho aur woh network drive ho, to `WNetGetConnection` se UNC nikaal ke wohi save karo (`Z:\...` → `\\10.5.48.2\XMLGateway\...`). Local drive ho to jaise hai. Fallback: convert na ho sake to saaf message "network drive letter nahi, `\\server\share` path chuno".

**Note (2026-10-06):** service NetworkService account pe chalti hai (install .bat line 56), usko bhi user ka Z: nahi dikhta, masla wahi hai. Installation Guide ka step 8 sahi tor pe UNC path `\\10.5.48.2\XMLGateway\BOOutBox` bolta hai, to jab tak client guide follow kare theek hai; fix us case ke liye hai jab koi Z: chun le.

**Moiez ka sawal (2026-10-07): dono pe nahi chal sakta?** Jawab: haan, user ke liye dono chal sakte hain, lekin service ke liye sirf UNC chalega, yeh Windows ka rule hai, code se nahi badalta. Drive letter us user ke logon session ki cheez hai; service alag session (session 0) mein alag account (NetworkService) pe chalti hai, uske liye `Z:` exist hi nahi karta. Isliye "dono" ka matlab yeh banta hai: Dashboard dono accept kare, user Z: chune ya `\\10.5.48.2\...`, lekin save hone se pehle Z: ko uske asli UNC mein badal de (`WNetGetConnection` Windows API, drive letter do, share ka UNC milta hai). DB mein hamesha UNC jaye, service ko Z: kabhi dikhe hi nahi. Local drive (C:, D:) ho to jaise hai. Mapped drive disconnected ho aur convert na ho sake to saaf message "yeh network drive hai, `\\server\share` wala path chuno". Dashboard mein ek chhota method, service mein kuch nahi. Faisla baaki.

### 19. Disable-RunExeAsAdmin.bat poori machine ka UAC prompt band karta hai — PENDING (deployment)

**Masla:** Installation Guide ka step 3 `Disable-RunExeAsAdmin.Bat` chalata hai, jo registry mein `ConsentPromptBehaviorAdmin = 0` set karta hai. Matlab us store PC pe har admin action bina UAC prompt ke chalega, sirf hamari app nahi, har program. Wajah samajh aati hai: `LdPosService.exe` ka manifest `requireAdministrator` hai (service start/stop aur ACL ke liye) aur client ko har baar prompt na dikhe.

**Behtar raaste (koi ek):** app ko ek scheduled task ke zariye "Run with highest privileges" se chalao (install .bat ek baar task banaye, shortcut usko trigger kare, prompt nahi aata, UAC poori machine pe on rehta hai). Ya app admin ke bagair chale aur service control ka kaam ek chhota elevated helper kare. Deployment policy ka faisla hai, Moiez ka call.

**DONE (Moiez: "19 kar do, Update-Disable-RunExeAsAdmin.bat ke naam se", 2026-10-08, commit d58099c):**
- Nayi `Update-Disable-RunExeAsAdmin.bat` Dropbox folder mein (purani `Disable-RunExeAsAdmin.Bat` ko haath nahi lagaya) aur repo ke `deploy\` mein (wahan purani wali hata di, git history mein hai). Teen kaam karti hai:
  1. `schtasks /Create` se task `LotteryDisplayPOS\LdPosService`: app highest privileges pe, sirf jab user logged in ho (`/IT`), schedule pe kabhi nahi chalta (`/SC ONCE /ST 00:00`, waqt guzar chuka), sirf shortcut se trigger.
  2. Public desktop pe shortcut "Lottery Display POS" jo `schtasks /Run /TN LotteryDisplayPOS\LdPosService` chalata hai, minimized, app ka icon. App bina UAC prompt ke elevated khulti hai.
  3. `ConsentPromptBehaviorAdmin` wapas Windows default 5 pe, taake purani .bat ka asar un PCs se bhi hat jaye jahan woh chal chuki hai.
- Shart: .bat usi Windows user se "Run as administrator" chale jo app use karega, aur woh user admin ho (task usi user ke naam banta hai). Guide mein likh diya.
- Test: shortcut banane wala PowerShell snippet dev machine pe scratchpad mein test kiya (target, args, style sahi). Task aur registry wala hissa yahan nahi chalaya, dev machine ki security setting nahi chhedni thi. Client pe pehli baar chalte waqt dekhna: desktop pe shortcut aaye, double-click pe app bina prompt khule.
- `Installation Guide.txt` bhi update: repo mein seedha, Dropbox mein `updated-Installation Guide.txt`. Steps ab: service install pehle, phir launcher setup, app shortcut se kholo, "Store Not Configured" ka matlab, Z: wali baat, log file ka raasta.

## Client logs analysis, 1-3 Oct 2026 (Wi-Fi wala store, purana March build)

Moiez ne 2026-10-08 ko teen log files di: `service-20261001.log` (5855 lines), `service-20261002.log` (1315), `service-20261003.log` (98). Build purana hai (stack trace mein `FileMonitorService.cs:line 212`, path `C:\Users\Maxymus\...`), naye fixes isme nahi hain.

**Din ka pattern:**
- 1 Oct (Wed): 09:56 start, subah do manual restart (09:57, 10:41). Din bhar 10:00-18:00 network stable, 280 files, 23 lottery, 19 upload sab OK. 17:11 se Wi-Fi girna shuru, raat bhar 6 outages.
- 2 Oct (Thu): raat 00:00-06:03 outages. 06:05 watcher fail, **deaf 06:05-17:30**. 17:30 manual restart, 20 files 30 minute mein. Raat 18:00 se phir outages, 12 total.
- 3 Oct (Fri): raat outages, 04:45 watcher fail, **deaf poora din**, log 04:45 pe khatam.

**Network ka pattern:** raat ko (roughly 17:00 se 06:00) share baar baar girta hai: up 1-3 minute, down 10-15 minute. Din mein stable. Lagta hai Wi-Fi AP ya Passport raat ko kuch karta hai (power save, reboot, backup). Yeh hamara masla nahi, lekin service ko isi mein zinda rehna hai.

**Jo confirm hua:**
| Point | Evidence |
|---|---|
| 4 (watcher fail → deaf) | 2 Oct 06:05 FileNotFoundException at StartRaisingEvents; 3 Oct 04:45 "Folder path is not accessible". Dono baar poora din deaf, sirf manual restart se theek. |
| 14 (Directory.Exists latakta hai) | Retry attempts ke beech 12-16 minute, code 30 second sota hai. 3 Oct: do checks ke beech 2m42s. |
| 1 (upload retry nahi) | 4 transactions 20 ghante late (21:27 fail, agle din 17:31 OK). 2 transactions 3 Oct tak pending. |
| 11 (lock wait) | 1 Oct 19:12:48 file detect, 19:16:16 "still locked after 10 retries" (3.5 minute, 35 second nahi, kyunke har check network pe latki). Turant baad "Win32Exception (53): The network path was not found", matlab file locked nahi thi, network gaya tha. File `PJR3402610011912022648.xml` dobara kabhi process nahi hui, gayi. |
| 3 (restart pe sab dobara) | 2 Oct 22:22 scan "Found 19", 22:39 "Found 24", 22:55 "Found 27": har recovery pe folder ki saari files dobara process aur upload. Naye code mein handled list se sirf nayi. |

**Naye code (GitHub, abhi deploy nahi) se kya badalta:** 2 Oct aur 3 Oct wale case mein watcher 1 minute baad dobara banta, deaf nahi hota. Uploads minutes mein. Locked/gayi file 5 minute baad scan uthata. Lekin point 14 ke bagair keep-alive loop outage mein har 14 minute chalega, 1 minute nahi, isliye 14 deploy se pehle zaroori.

## Background facts

- Store mein 2 Gilbarco Passport terminals. Ek waqt mein max 2 XML files.
- Backend `lotteryscreen.app` (production). Feb 2026 wala version `stage.lotteryscreen.app` pe tha.
- Git: 2026-10-06 ko purana repo `POS-DesktopApp-C-` delete kar ke naya `LD-GilbarcoFileProcessor` banaya. `LdPosService` folder hi repo hai. `..\POS-DesktopApp-C` clone stale hai, wahan se push mat karo.
- Publish: `FolderProfile.pubxml` → `C:\ProgramData\LotteryDisplayPOS\LdFileProcessor`, self-contained single-file win-x64. Yeh file gitignore mein hai (`*.pubxml`).
- Runtime data: `C:\ProgramData\LdPosService\` mein `PosData.db`, `logs\service-YYYYMMDD.log`, `TempFiles\`.
- Dev machine pe `C:\ProgramData\LdPosService\logs\` ke July 2026 logs ek doosri service ke hain (LdOposService: Verifone auth, CoreScanner barcode). Woh bhi same folder aur same `service-.log` naam use karti hai. Agar store machine pe dono services saath chalein to Serilog ka file sink ek waqt mein ek process ko hi file deta hai, doosri ke logs chup chaap gayab honge. Sawal: dono ek machine pe chalti hain? (Jawab: nahi, point 17.)
- Deployment package: `E:\Dropbox\LD shared\Debug App\POS-Gilbarco-CSharp\LotteryDisplayPOS\` mein `Install-WindowService.bat`, `Unistall-WindowService.bat`, `Disable-RunExeAsAdmin.Bat`, `Installation Guide.txt`, aur `LdFileProcessor\`, `LdPosService\` ke exe (2026-03-09 ke builds, matlab client pe abhi March wala code hai). Client steps: folder `C:\ProgramData\LotteryDisplayPOS` mein copy, UAC .bat, install .bat (service NetworkService pe banti hai, start nahi hoti), `LdPosService.exe` admin se, barcode se login (format `LDSS.430.06107801`), Browse Folder se `\\10.5.48.2\XMLGateway\BOOutBox`, popup "service started". Server side pehle Store Settings mein Pos Lottery Dept ID aur Pos Payout Dept ID. 2026-10-06 se Dropbox mein `updated-Install-WindowService.bat` bhi hai (crash recovery ke saath), aur yahi scripts repo ke `deploy\` folder mein versioned hain; aage se deploy folder repo se Dropbox mein copy hona chahiye, ulta nahi. 2026-10-08 se Dropbox mein `Update-Disable-RunExeAsAdmin.bat` aur `updated-Installation Guide.txt` bhi hain. Jab client sab update ho jayein, Dropbox se purani `Disable-RunExeAsAdmin.Bat`, `Install-WindowService.bat` aur `Installation Guide.txt` hata do aur nayi files ke naam se "updated-"/"Update-" prefix nikaal do; repo ka `deploy\` folder wahi final naam rakhta hai (sirf `Update-Disable-RunExeAsAdmin.bat` ka naam Moiez ki marzi se aisa hi hai).

## Commits (sirf code; notes ke commits yahan nahi)

| Commit | Date | Kya |
|---|---|---|
| 9b51aeb | 2026-10-06 | Initial commit, March 2026 production code |
| 04597f3 | 2026-10-06 | Point 1 (+7): periodic upload retry, semaphore lock, break on first failure |
| 27b1499 | 2026-10-06 | Point 4: TrySetupFileWatcher, keep-alive loop recreates watcher, 5-minute folder scan safety net |
| e6e2af5 | 2026-10-06 | Point 4 refinement: periodic scan finds missed files -> recreate watcher |
| 71ef215 | 2026-10-06 | deploy\: install .bat with sc failure + failureflag, uninstall, UAC .bat, Installation Guide |
| 6e49536 | 2026-10-07 | Point 15: dead code, Dapper, IsProcessed column, Serilog.AspNetCore, duplicate icon in resx |
| 96bdd78 | 2026-10-07 | Point 18: Dashboard converts mapped drive letter to UNC before saving (WNetGetConnection) |
| 629989f | 2026-10-07 | Point 11: lock wait 60 x 500ms = 30s via constants, log text derived from them |
| 082929d | 2026-10-07 | Point 9 (+10): ILogger in ApiLibrary, HTTP status/body logged on failures, inner exceptions kept, login JSON serialized |
| 7190a87 | 2026-10-08 | Point 14: path check / watcher create / scan listing / copy with timeouts, single in-flight Exists, lock wait by wall clock |
| 66192af | 2026-10-08 | Point 6: login refused when pos_dept_id is 0 (token released), saved DeptId-0 user removed on start, Dashboard shows store/dept, service logs ERROR |
| d58099c | 2026-10-08 | Point 19: Update-Disable-RunExeAsAdmin.bat (elevated scheduled task + desktop shortcut, UAC default restored), Installation Guide rewritten |
