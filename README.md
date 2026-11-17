# Codex Agent Platform

.NET 10 hedefleyen Blazor Interactive Server uygulaması. Yönetici, sohbetten Codex ajanlarıyla çalışır. Konuşmalar ve görev olayları SQLite’ta, sürümlenmiş bilgi notları Markdown dosyalarında saklanır. Arka plan çalışması tarayıcı bağlantısından bağımsızdır.

## Visual Studio veya komut satırından çalıştırma

1. Visual Studio’da `CodexAgentPlatform.slnx` dosyasını açın. Başlangıç projesi `CodexAgentPlatform`, profil `https` olsun.
2. HTTPS sertifikası ilk kez kullanılacaksa `dotnet dev-certs https --trust` çalıştırın.
3. CLI’nin kurulu olduğunu `codex --version` ile kontrol edin. Windows geliştirme profilinde uygulama, PATH’te bulunamayan CLI için Codex masaüstü kurulumuna da bakar. Özel bir kurulumda `Platform__CodexExecutable` ayarına yürütülebilir dosyanın tam yolunu verin.
4. İlk açılışta kurulum sayfasında yeni yönetici adınızı ve en az 14 karakterlik güçlü şifrenizi belirleyin. Geliştirmede doğrudan loopback bağlantısı ve `Platform__LocalBootstrap=true` varsa geçici şifre `admin` kabul edilir. Diğer ortamlarda kurulum kodunu veri dizinindeki `credentials/bootstrap-code` dosyasından okuyun. Kurulum tamamlandığında geçici erişim geçersiz olur.
5. **Codex ayarları** ekranında bağlantıyı test edin ve **Cihaz koduyla giriş yap** düğmesini kullanın. Cihaz kodu seçeneği hesabınızın veya çalışma alanınızın güvenlik ayarlarında etkin olmalıdır. Oturum bu uygulamanın `data/codex` dizininde tutulur.
6. **Ajanlar** ekranında ajan rollerini, proje izinlerini, model ve reasoning effort ayarlarını düzenleyin. Sohbette ajan seçebilir veya `@Developer`, `@Architect`, `@QA`, `@DevOps` ile başlayabilirsiniz.

```powershell
dotnet run --project CodexAgentPlatform --launch-profile https
```

App Server, uygulama tarafından JSONL üzerinden `stdio` ile başlatılır. Uygulama tek yöneticiye ait yerel kullanım için tasarlanmıştır. App Server hesabıyla kimlik doğrulamanın kapsamı ve ayrı barındırılan servis akışı için [OpenAI’nin App Server belgelerine](https://learn.chatgpt.com/docs/app-server#auth-endpoints) bakın. [Headless cihaz kodu akışı](https://learn.chatgpt.com/docs/auth#login-on-headless-devices).

## Docker Compose

Compose yönetim arayüzünü yalnızca `https://localhost:8443` üzerinde yayınlar. Sertifikayı ve ayrı bir AES-256 Vault anahtarını repo dışında oluşturup örnekteki `secrets` yollarına yerleştirin. Örnekteki PowerShell komutları yerel geliştirme sertifikası kullanır.

```powershell
New-Item -ItemType Directory -Force secrets
$env:PLATFORM_TLS_PASSWORD = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
dotnet dev-certs https --trust
dotnet dev-certs https -ep secrets/platform.pfx -p $env:PLATFORM_TLS_PASSWORD
[IO.File]::WriteAllText((Join-Path $PWD 'secrets/vault.key'), [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
docker compose up -d --build
docker compose exec platform cat /data/credentials/bootstrap-code
```

- Docker image’ı .NET 10, Git, Python, Bubblewrap ve sabitlenmiş Codex CLI içerir. Servis `app` kullanıcısıyla çalışır. Docker secret’ları bu kullanıcı tarafından okunabilir olmalıdır.
- `/data` ve `/workspaces` ayrı adlandırılmış volume’larda kalır. Yeniden kurulumda aynı Vault anahtarıyla çalışın. `docker compose down -v` bu iki volume’u kaldırır.
- Linux ve WSL’de dosya sistemi kısıtları Bubblewrap’e dayanır. Windows’ta kurulu CLI’nin desteklediği sandbox hazır olmalıdır. Container platformu Bubblewrap’in gerektirdiği namespace işlemlerine izin vermiyorsa terminal komutları başarısız olur. [Codex sandbox platform gereksinimleri](https://learn.chatgpt.com/docs/permissions#how-enforcement-works).
- Windows’ta **Codex ayarları** sandbox durumunu gösterir. MXC kullanılamıyorsa yerel yönetici onayıyla yükseltilmiş Windows sandbox’ı kurulabilir. Kurulum yapılana kadar sohbet yerel çalışma ortamı kapalı olarak çalışır. [Windows sandbox seçenekleri](https://learn.chatgpt.com/docs/windows/windows-sandbox).
- **Sistem** ekranındaki SQLite yedeği çevrimiçi alınabilir. Tam geri yükleme için `/data` ve `/workspaces` volume’larını tutarlı biçimde, Vault anahtarını ayrı saklayın.

## Proje erişimi ve çalışma geçmişi

- Önce **Projeler** ekranında projeyi oluşturun, ardından **Kaynaklar** ekranında Git, SSH veya dağıtım kaynağını kaydedin. **Ajanlar** ekranında ilgili izinleri verin ve sohbet kutusunda projeyi seçin.
- Git kaynağı HTTPS adresi kullanır. Opsiyonel Git kimlik bilgisi ve SSH private key, Vault anahtar dosyası yapılandırıldığında şifrelenir. Ajan araçları Vault değerini almaz.
- Git değişiklikleri yönetilen worktree’de yapılır. Push onayı kaynak adresi, kaynak branch, hedef branch ve tam commit’e bağlanır. Üretim onayı bu kapsamı ekranda gösterir. Push force seçeneği kullanmaz.
- SSH yapılandırması doğrulanmış `SHA256:` host key fingerprint ve private key gerektirir. Hazır komut setinde servis ve Docker incelemesi, bağlantı testi ve kayıtlı bir SFTP metin dosyasını okuma vardır. Docker restart, digest’e sabitlenmiş Swarm dağıtımı ve Swarm rollback açık onay gerektirir.
- Konuşma, çalışma durumu, bilgi notları ve olay geçmişi ayrı saklanır. Hafıza araçları arama sonuçlarını sınırlar, gerektiğinde Markdown notunun bir bölümünü getirir. Başka bir sohbetten zamana göre çalışma bulma, oluşturma tarihinin yanında kaydedilmiş olayları kullanır.
- Bir işlem sırasında **Durdur** düğmesini kullanabilir veya `Şimdilik dur` yazabilirsiniz. Uygulama yeniden başlarsa yarıda kalan turn ve onaylar kesilmiş olarak işaretlenir. Yeni mesajla devam edildiğinde Codex’in kayıtlı thread’i ve çalışma dizini kullanılır.

## Testler

```powershell
dotnet test CodexAgentPlatform.slnx
$env:CAP_TEST_CODEX_EXE = (Get-Command codex).Source
$env:CAP_TEST_GIT_EXE = (Get-Command git).Source
dotnet test CodexAgentPlatform.slnx
```

Testler HTTPS oturumu, ilk kurulum, migration, SQLite yedeği, FTS5, hafıza sürümleri, zamansal arama, ajan izinleri, mock streaming, oturum kurtarma ve token tekilleştirmesini kapsar. Yürütülebilir yollar sağlandığında kurulu CLI ile hesapsız protokol testi ve gerçek bir Git worktree’de push onayı kapsam testi de çalışır. Gerçek hesapla streaming testi için, cihaz doğrulaması yapılmış test kurulumunun veri dizinini ayrıca `CAP_TEST_AUTH_DATA` değişkenine verin. Bu test gerçek Codex isteği gönderir.

## Ayarlar

| Ayar | Varsayılan |
| --- | --- |
| `Platform__Data` | Web projesi altında `data` |
| `Platform__Workspaces` | Web projesi altında `workspaces` |
| `Platform__CodexExecutable` | PATH üzerindeki `codex`. Windows geliştirme profilinde Codex masaüstü kurulumu da aranır. |
| `Platform__LocalCodexLogin` | Geliştirme profilleri ve loopback Compose örneğinde `true` |
| `Platform__Concurrency` | `2`, geçerli aralık 1–4 |
| `Platform__LocalBootstrap` | `false`, yalnızca geliştirme ve doğrudan loopback için etkinleştirin |
| `Platform__AllowLocalHttp` | `false`, yalnızca geliştirme profili için etkinleştirin |
| `Platform__VaultKeyFile` | 32 baytlık base64 anahtarı içeren harici dosya |

Şema değişiklikleri için EF Core migration oluşturun. Başlangıçta bekleyen göçler uygulanır. FTS5 sanal tabloları ve trigger’ları ilk migration’da tanımlanır.

```powershell
dotnet ef migrations add MigrationName --project CodexAgentPlatform.Infrastructure --startup-project CodexAgentPlatform
```
