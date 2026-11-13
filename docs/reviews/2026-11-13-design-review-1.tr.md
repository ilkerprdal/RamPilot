# Fable görüşü — tasarım incelemesi (13.11.2026)

Belgelere ve Faz 0 verisine dayanır; yeni ölçüm yapılmadı. "Doğrula" yazanlar varsayımdır.

## 1. Sinyal
- **Ana kaynak:** `NtQuerySystemInformation(SystemProcessInformation)`. Tek çağrıda tüm süreçler için WS, private WS, `PageFaultCount`, **`HardFaultCount`**, cycle time verir; yönetici gerekmez, WMI/PDH'den çok ucuzdur.
- **Refault tanımı:** yalnız WS ≥ sınırın %95'indeyken ölçülen `PageFaultCount` farkı. Sınırdayken her hata bir sayfa çıkarır; WS sınırın altındaysa baskı yoktur.
- **Maliyet sinyali:** `HardFaultCount` farkı + "Memory Compression" sürecinin CPU'su. Depodan geri okumanın hard fault sayıldığını Faz 1'de doğrula.
- ETW kernel bellek olayları yönetici ister: ele. `PROCESS_MEMORY_COUNTERS_EX2` hata türü vermez; yalnız kazanç ölçümünde kullan (`PrivateWorkingSetSize`).
- `QueryWorkingSetEx` erişim biti vermez ve pahalıdır; sürekli sinyal olmaz, yalnız sıcak sayfa anlık görüntüsü için.

## 2. Denetleyici
- PI gereksiz; sinyal gürültülü. **Yavaş in, hızlı çık:** refault düşükse 30–60 sn'de sınır ×0,9; eşik aşılırsa hemen ×1,5–2. Ölü bant ekle.
- **Ufuk sorunu:** 60 sn'de 13–16 MB, sınır kalkınca 5 dk'da 120 MB. Periyodik bir şey (muhtemelen GC) soğuk sayfalara dokunuyor. Dar sınırda bu, sıkıştır/aç fırtınası olur. Sınırı en az 5–10 dk'lık pencereyle değerlendir.
- **Yoklama:** boşta girişte tek seferlik kırpma + 60 sn yeniden büyüme = H. Sınır = max(32 MB, 1,5×H + 16 MB).
- **Odak:** `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT)` ile tüm süreç ağacında sınırı anında kaldır; 30–60 sn geri koyma.
- **Ön ısıtma (deneysel):** sınır öncesi kaydedilen sıcak adresleri `ReadProcessMemory` ile okumak sayfaları hedefin WS'sine geri getirir (doğrula). `PROCESS_VM_READ` ister, AV'de şüphe çeker.

## 3. Tuzaklar
- **Çökme güvenliği:** RamPilot ölürse sert sınırlar kalıcıdır. Sınırlı PID + başlangıç zamanını diske yaz, açılışta geri al; PID yeniden kullanımına karşı handle'ı açık tut.
- Sınır çocuk süreçlere miras kalmaz; ağacı izle. `--type=gpu-process`, ses servisi ve crashpad'i hariç tut (`NtQueryInformationProcess(ProcessCommandLineInformation)`).
- Sesli arama/bildirim sırasında sınırda thrash = takılma. Ses oturumu (`IAudioSessionManager2`) ve CPU artışı da sınırı kaldırsın.
- Hedef kendi `SetProcessWorkingSetSize` çağrısıyla bayrakları siler; her turda `GetProcessWorkingSetSizeEx` ile denetle.
- Paylaşımlı sayfalar: WS toplamı kazancı şişirir. Private WS + Available + depo büyümesiyle raporla.
- `ProcessMemoryPriority` düşürme: standby sayfaları erken yeniden kullanılır, soft refault hard'a döner. EcoQoS zararsız ama uyanışı yavaşlatır.
- SearchHost zaten PLM ile askıda; kazanç küçük olabilir, ölç.
- **Anti-cheat:** yalnız izin listesindeki süreçlere handle aç; oyuna ve anti-cheat süreçlerine asla dokunma.

## 4. Daha fazla kazanç
- **Dondurma + tam kırpma** (uzun boşta, uygulama bazında onaylı): sıcak küme 0'a iner, 120 MB'a geri büyüme durur. Bildirimler kesilir.
- **`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS`** kullanıcı ortam değişkeni: enjeksiyonsuz Chromium bayrağı (örn. `--js-flags=--max-old-space-size`). Commit'i de düşürür; WS sınırı düşürmez.
- Page combining: Chromium'da beklenti düşük.

## 5. Hüküm
Mimari sağlam; mekanizma doğru, Faz 0 hipotezi destekliyor. Asıl risk kazanç değil, uyanış gecikmesi ve kalıcı sınır. Windows baskı altında bu sayfaları zaten kırpar: gerçek fayda bellek darken görünür, dürüst raporla.

İlk 3 değişiklik:
1. Çökmeye dayanıklı geri alma + süreç ağacı/rol filtreleme, denetleyiciden önce.
2. Sinyali `HardFaultCount` + sınırdaki hata hızına, ufku 5–10 dk'ya çek; sınırı gevşek tut.
3. Sınır kaldırmayı odak dışında ses ve CPU etkinliğine de bağla; odak gecikmesini insan testiyle ölç.
