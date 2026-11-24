# Codex Agent Platform

.NET 10 ve Blazor Interactive Server ile çalışan tek yöneticili Codex arayüzü. Sohbetler ve uygulama kayıtları SQLite’ta, proje dosyaları ve hafıza notları diskte saklanır.

## Yeni sunucuya ilk kurulum

Bu adımlar boş bir Ubuntu sunucusunda, HTTPS’i host üzerindeki Nginx’in karşıladığı kurulum içindir. Örneklerdeki `platform.example.org` yerine kendi DNS adınızı kullanın. Komutları sunucuda yönetici olarak Bash ile çalıştırın.

### 1. Sunucuyu hazırlayın ve hazır imajı indirin

Docker Engine çalışıyor olmalı. Nginx, Certbot ve Nginx eklentisi, curl, tar ve sandbox için etkin AppArmor ile `apparmor_parser` gerekir. Alan adını sunucuya yönlendirin ve Nginx’in 80 ve 443 numaralı portlarına dışarıdan erişim sağlayın. [.NET çalışma zamanı, Codex CLI ve Bubblewrap hazır imajda gelir](Dockerfile).

Paket özel ise indirmeden önce GHCR’ye oturum açın. Paketi okumaya yetkili GitHub kullanıcı adınızı ve parola sorulduğunda paket okuma yetkili belirtecinizi girin.

```bash
docker login ghcr.io
```

```bash
docker info >/dev/null && docker pull ghcr.io/unholywar/codexagentplatform:latest
```

### 2. Ağı, kalıcı diskleri ve ilk container’ı oluşturun

Aşağıdaki komut ilk kurulum içindir. Docker, adlandırılmış volume’ları ilk kullanımda oluşturur. Güncellemede çalışan container’ın bağlantılarını devralan 7. adımı kullanın.

```bash
docker network inspect codex-agent-backend >/dev/null 2>&1 ||
  docker network create codex-agent-backend

docker run -d --name codex-agent-platform \
  --init --restart unless-stopped \
  --network codex-agent-backend \
  -p 127.0.0.1:18090:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e 'ASPNETCORE_URLS=http://+:8080' \
  -e ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
  -e Platform__LocalCodexLogin=true \
  -v codexagentplatform_platform-data:/data \
  -v codexagentplatform_platform-workspaces:/workspaces \
  ghcr.io/unholywar/codexagentplatform:latest
```

`ASPNETCORE_FORWARDEDHEADERS_ENABLED` ile uygulama Nginx’in ilettiği HTTPS bilgisini okur. Sunucudan `curl --max-time 5 -sS -o /dev/null -w '%{http_code}\n' http://127.0.0.1:18090/` ile uygulamanın yanıt verdiğini kontrol edin. Oturumsuz istekte genellikle girişe yönlendiren `302` görülür. Bu kontrol yalnızca yerel HTTP erişimini sınar. Tarayıcıdaki kurulum ve oturum açma akışı güvenli çerez kullandığından aşağıda hazırlanan HTTPS adresini kullanın. Host portunu loopback’e bağlı tutun ve `codex-agent-backend` ağına başka container bağlamayın.

### 3. Nginx ve HTTPS

`/etc/nginx/sites-available/platform.example.org` dosyasını oluşturun. Nginx’in yüklediği mevcut ana site yapılandırmasını ayrı tutun.

```nginx
map $http_upgrade $codex_connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 80;
    server_name platform.example.org;

    location / {
        proxy_pass http://127.0.0.1:18090;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $codex_connection_upgrade;
    }
}
```

DNS kaydı çözülüyor ve sunucuda Certbot’un Nginx eklentisi hazırsa siteyi etkinleştirin.

```bash
ln -s /etc/nginx/sites-available/platform.example.org \
      /etc/nginx/sites-enabled/platform.example.org
nginx -t && systemctl reload nginx &&
  certbot --nginx -d platform.example.org --redirect
```

### 4. İlk kurulum ve bağlantı

```bash
curl -sS -o /dev/null -w 'HTTP %{http_code}\n' \
  https://platform.example.org/license
docker exec codex-agent-platform cat /data/credentials/bootstrap-code
```

Tarayıcıda kendi alan adınızdaki `/license` sayfasını HTTPS üzerinden açın. Yeni kurulumda yukarıdaki kurulum kodunu ve lisans panelinden aldığınız lisans kodunu girin. Aktivasyon başarılı olunca `/setup` üzerinden aynı kurulum koduyla yönetici hesabını ve en az 14 karakterlik parolasını oluşturun. **Mevcut kurulumda** aktivasyon için kayıtlı yöneticinin adı ve parolası gerekir. Başarıdan sonra `/login` üzerinden giriş yapın.

Lisans kodunu yalnızca HTTPS aktivasyon formuna girin. Kurulum kimliği, yenileme için gerekli kod ve imzalı belge `/data/credentials/platform-license.json` içinde saklanır. Bu dosyayı içeren yedekleri erişimi sınırlı bir yerde tutun. Aynı veri volume’unu kullanın. Uygulama başlangıçta ve beş dakikada bir yenileme dener. Geçici bağlantı hatasında geçerli imzalı belge bitişine kadar kullanılabilir. Lisans servisinden gelen `403` yanıtı yerel yetkiyi kaldırır. Yönetici `/license` üzerinden tekrar etkinleştirebilir.

İletişim bilgisi için container ortamında `License__ContactEmail` veya `License__LinkedIn` tanımlayın. Yerel `/healthz` yanıtı yalnızca işlemin çalıştığını gösterir. Üretime geçişi doğrulamak için ayrıca HTTPS üzerinden aktivasyon ve yönetici girişi yapıp korunan bir sayfayı açın. Yönetici menüsündeki **Yeni sürüm var** bağlantısı, sunucuda çalıştırılacak sürüme sabitlenmiş komutları gösterir.

**Codex ayarları → Cihaz koduyla giriş yap** üzerinden çıkan tek kullanımlık kodu OpenAI doğrulama sayfasına girin. ChatGPT hesabınızın güvenlik ayarlarında cihaz koduyla giriş etkin olmalıdır. Ardından **Bağlantıyı test et** ile hesabı ve model listesini kontrol edin. [Cihaz koduyla oturum açma](https://learn.chatgpt.com/docs/auth#login-on-headless-devices)

`Platform__LocalCodexLogin=true` web arayüzünde yeni hesap bağlama düğmesini açar. Bu seçenek kapalıyken App Server, diskteki kayıtlı hesabı kullanabilir. `Production` modu HTTPS ve güvenli çerezleri yönetir. Hesap oturumu `/data/codex` altında kalır ve normal yeniden başlatmalarda kullanılır.

Düğmeler tıklamaya yanıt vermiyorsa Blazor betiğinin yanıtını kontrol edin.

```bash
curl -sS -D - -o /dev/null \
  https://platform.example.org/_framework/blazor.web.js
```

HTTP `200` ve JavaScript içerik türü beklenir. Betik `404` dönüyorsa framework dosyalarını içeren güncel imajı kullanın.

### 5. Terminal, çalışma alanı ve Vault’u etkinleştirin

İlk container çalışırken güvenilir depo kopyasındaki [sandbox dizinini](deploy/docker-sandbox) profil dosyalarıyla birlikte sunucuya aktarın. Aşağıdaki örneği sunucudaki depo kökünden, **ilk kurulumda** çalıştırın. Arşivi sonraki güncellemelerde de kullanacaksınız.

```bash
(
  set -e
  tar -C deploy -czf /root/codex-docker-vault.tar.gz docker-sandbox
  bash deploy/docker-sandbox/enable.sh --enable-vault
)
```

[enable.sh](deploy/docker-sandbox/enable.sh) çalışan container’ı devralır. Hostta AppArmor ve seccomp profillerini yükler, yeni container’ı Linux capability’lerini kaldırarak ve `no-new-privileges` ile başlatır. Böylece Codex, terminal komutlarını Bubblewrap içinde sınırlar. Betik ilk Vault anahtarını ayrı bir volume’da oluşturur ve uygulamaya salt okunur bağlar. Mevcut şifreli kayıtlar varsa özgün anahtar gerekir. [Sandbox profillerinin kaynağı](https://github.com/openai/codex-security/blob/main/docker/README.md)

## Mevcut üretim kurulumu ve güncelleme

### 6. Güncelleme öncesinde çalışan düzeni ve yedekleri kontrol edin

“Sunucum” kurulumunda Nginx, HTTPS isteklerini `codex-agent-platform` container’ına iletir. Betik bu isimle çalışan, kullanıcısı `app`, ağı `codex-agent-backend` ve port eşlemesi `127.0.0.1:18090:8080` olan kurulumu bekler. `/root/codex-docker-vault.tar.gz`, betiği ve iki güvenlik profilini taşır. Özgün Vault anahtarı ayrı disktedir. Sunucu değiştirirken uygulama verilerini, çalışma alanlarını ve özgün anahtarı birlikte yedekleyin. Güncellemeden önce geri yüklenebilir, tutarlı bir yedeğiniz olduğunu doğrulayın.

```bash
docker inspect -f '{{.State.Status}} {{.Config.User}} {{.HostConfig.NetworkMode}}' codex-agent-platform
docker port codex-agent-platform
docker inspect -f '{{range .Mounts}}{{println .Destination .Type .Name .RW}}{{end}}' codex-agent-platform
tar -tzf /root/codex-docker-vault.tar.gz
```

### 7. Hedef commit’i doğrulayarak yeni imaja geçin

`master` dalına push edildikten sonra GitHub Actions’ta hedef commit için **Publish Docker image** işinin başarıyla bitmesini bekleyin. [Yayın iş akışı](.github/workflows/main.yml) imajı GHCR’ye `latest` etiketiyle gönderir ve tam commit kimliğini `org.opencontainers.image.revision` alanına yazar. GitHub’dan hedef commit’in 40 karakterlik SHA değerini kopyalayın. Aşağıdaki bloğu çalışan sunucuda yönetici olarak çalıştırın.

```bash
(
  set -euo pipefail
  read -rp 'Hedef commit (40 karakterlik SHA): ' expected
  [[ $expected =~ ^[0-9a-f]{40}$ ]] || { echo 'Tam commit SHA gerekli.' >&2; exit 1; }

  image=ghcr.io/unholywar/codexagentplatform:latest
  name=codex-agent-platform
  docker pull "$image"
  image_id=$(docker image inspect -f '{{.Id}}' "$image")
  revision=$(docker image inspect -f '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$image_id")
  [[ $revision == "$expected" ]] || { echo "İndirilen commit farklı: $revision" >&2; exit 1; }

  install_dir=$(mktemp -d)
  trap 'rm -rf -- "$install_dir"' EXIT
  tar -xzf /root/codex-docker-vault.tar.gz -C "$install_dir"
  bash "$install_dir/docker-sandbox/enable.sh" --image "$image_id" --enable-vault

  running=$(docker inspect -f '{{.State.Running}} {{.Image}}' "$name")
  [[ $running == "true $image_id" ]] || { echo 'Çalışan imaj doğrulanamadı.' >&2; exit 1; }
  status=$(curl --max-time 5 -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:18090/)
  case "$status" in
    200|302) printf 'Çalışan commit %s, yerel HTTP %s\n' "$revision" "$status" ;;
    *) echo "Beklenmeyen HTTP yanıtı: $status" >&2; exit 1 ;;
  esac
)
```

### 8. Geçiş sonucunu ve geri dönüş durumunu okuyun

1. Betik mevcut ortam ayarlarını ve disk bağlantılarını devralır. Adayın imajını ve bağlantılarını kontrol ettikten sonra eski container’ı durdurur. Bu sırada kısa bir kesinti olur.
2. Yeni container başlayınca betik yerel HTTP yanıtını, Bubblewrap’in çalışmasını ve geçici çalışma alanına yazmayı sınar. Sandbox’ın Codex yapılandırmasını, deneme amaçlı gizli dosyayı ve yapılandırılmış Vault anahtarını okuyamadığını kontrol eder. Geçiş başarılıysa önceki container, `codex-agent-platform-before-sandbox-<UTC zaman damgası>` adıyla durdurulmuş halde saklanır.
3. Betiğin geçiş aşaması hata verirse aday container kaldırılmaya çalışılır. Eski container’ın adı geri alınır ve yeniden başlatılması denenir. Başlatma başarısızsa betik eski container kimliğini bildirir. Bu geri dönüş ortak volume’lardaki veri değişikliklerini geri sarmaz.
4. Seçilen imaj zaten çalışıyorsa ve Vault yapılandırması gerekmiyorsa betik erken çıkar. Bu durumda sandbox testleri yeniden çalışmaz. Yukarıdaki son imaj ve HTTP kontrolleri yine çalışır. Bu dış kontrollerdeki hata otomatik geri dönüş başlatmaz.

İsteğe bağlı olarak eski imajları temizlemek için çalışan kurulumda `bash deploy/docker-sandbox/enable.sh --cleanup-old-images` kullanabilirsiniz. Betik en son geri dönüş container’ını korur. Daha eski, durmuş ve etiketsiz platform imajına bağlı yedek container’ları, ardından kullanılmayan etiketsiz platform imajlarını temizler.

## Kalıcı veriler

- `codexagentplatform_platform-data` → `/data`. SQLite, sohbetler, hafıza notları, Codex oturumu ve uygulama verileri.
- `codexagentplatform_platform-workspaces` → `/workspaces`. Proje çalışma alanları.
- Yeniden dağıtırken aynı volume adlarını kullanın. Mevcut kurulum farklı bir Compose proje adı kullanıyorsa önce `docker volume ls` ile gerçek adları bulun. **Sistem** ekranından SQLite yedeği alabilirsiniz.

## Private Git ve SSH kaynakları

Uygulama Git erişim belirteçlerini ve SSH özel anahtarlarını şifreleyerek `/data/credentials` altında saklar. 5. adımda oluşturulan `codexagentplatform_vault-key` volume’u `/run/platform-vault` altında salt okunur bağlanır. Uygulama `Platform__VaultKeyFile=/run/platform-vault/master.key` yolunu kullanır. Güncelleme sırasında mevcut anahtarı koruyun. Yalnızca SQLite yedeği, şifrelenmiş erişim bilgilerini açmaya yetmez.

- **Kaynaklar → Kaynak ekle** ekranında **Projeye özel** veya **Global** kapsamını seçin. Global kaynaklar proje seçmeden eklenebilir ve kaynak listesinde yıldızla görünür.
- **GitRepository** eklerken HTTPS depo adresini girin. Özel depo için **Özel depo** seçeneğini işaretleyip erişim belirtecini (PAT) girin. GitHub’da belirteci ilgili depoyla sınırlayın. Klonlama ve pull için okuma, push için yazma yetkisi gerekir. [GitHub erişim belirteçleri](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens)
- **SshServer** eklerken container içinden erişilebilen hostname veya IP’yi girin. Kullanıcı ve port varsayılanları `root` ve `22`. Uzak sunucunun izin verdiği kullanıcıyı ve portu seçin. Bağlantıya özel bir SSH anahtar çifti hazırlayın. `.pub` dosyasını o kullanıcının `~/.ssh/authorized_keys` dosyasına ekleyin, parolasız özel anahtarı ise uygulamadaki **SSH özel anahtarı** alanına girin. [OpenSSH yetkili anahtarlar](https://man.openbsd.org/sshd#AUTHORIZED_KEYS_FILE_FORMAT)
- **Sunucu anahtarının parmak izi** alanına hedef SSH sunucusunun doğrulanmış `SHA256:…` değerini girin. Örneğin Ed25519 host anahtarı için hedef sunucuda `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` çalıştırın. Kaydettikten sonra DevOps Engineer’a aynı projedeki SSH kaynağının bağlantısını test etmesini söyleyin. [OpenSSH parmak izi](https://man.openbsd.org/ssh-keygen#l)

Depodaki [Docker Compose dosyası](docker-compose.yml), TLS’i uygulamanın karşıladığı alternatif loopback kurulumudur. Bu seçenekte PFX, sertifika parolası ve Vault anahtarı gerekir.

## Yerel geliştirme

Depo kökünden çalıştırın. Visual Studio’da `CodexAgentPlatform.slnx` dosyasını ve `https` profilini seçebilirsiniz.

```powershell
dotnet dev-certs https --trust
dotnet run --project CodexAgentPlatform --launch-profile https
dotnet test CodexAgentPlatform.slnx
```

Yerel adres [https://localhost:7213](https://localhost:7213). Codex CLI’nin kurulu olduğunu `codex --version` ile kontrol edin. Özel CLI yolu için `Platform__CodexExecutable` kullanılır. Yerel terminal işlemleri için [Codex sandbox gereksinimlerine](https://learn.chatgpt.com/docs/permissions#how-enforcement-works) bakın.
