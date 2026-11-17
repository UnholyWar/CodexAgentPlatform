# Codex Agent Platform

.NET 10 ve Blazor Interactive Server ile çalışan tek yöneticili Codex arayüzü. Sohbetler ve uygulama kayıtları SQLite’ta, proje dosyaları ve hafıza notları diskte saklanır.

## Canlı kurulum

Sunucuda Docker ve host üzerinde Nginx gerekir. Örnekte `development.onurozgultekin.com` kullanılır. Alt alan adının A kaydını sunucunun genel IPv4 adresine yönlendirin. HTTPS’i Nginx karşılar. Uygulama host üzerinde yalnızca `127.0.0.1:18090` adresinden dinlenir.

### 1. İsteğe bağlı HTTP başlangıç kontrolü

İlk kurulumda imajın açıldığını kontrol etmek için aşağıdaki geçici container’ı çalıştırın. Canlı container çalışıyorsa bu adımı atlayın.

```bash
docker run -d --name codex-agent-platform-check \
  -p 127.0.0.1:18090:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e 'ASPNETCORE_URLS=http://+:8080' \
  -v codexagentplatform_platform-data:/data \
  -v codexagentplatform_platform-workspaces:/workspaces \
  ghcr.io/unholywar/codexagentplatform:latest

docker logs --tail 50 codex-agent-platform-check
curl -sS -o /dev/null -w 'HTTP %{http_code}\n' http://127.0.0.1:18090/
```

Oturum açmadan ana adrese yapılan istekte `302` beklenir. `/setup` formu güvenli çerez kullandığı için doğrudan HTTP isteğinde `500` verir. Kurulum formunu aşağıdaki HTTPS adresinden açın.

### 2. Canlı container’ı çalıştır

Başlangıç kontrolü container’ı çalışıyorsa `docker stop codex-agent-platform-check` ile durdurun. Aynı volume’ları kullanan önceki uygulama container’ı varsa onu da yeni container’ı başlatmadan önce durdurun.

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

`ASPNETCORE_FORWARDEDHEADERS_ENABLED` ile uygulama Nginx’in ilettiği HTTPS bilgisini okur. Host portunu loopback’e bağlı tutun ve `codex-agent-backend` ağına başka container bağlamayın.

### 3. Nginx ve HTTPS

`/etc/nginx/sites-available/development.onurozgultekin.com` dosyasını oluşturun. Nginx’in yüklediği mevcut ana site yapılandırmasını ayrı tutun.

```nginx
map $http_upgrade $codex_connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 80;
    server_name development.onurozgultekin.com;

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
ln -s /etc/nginx/sites-available/development.onurozgultekin.com \
      /etc/nginx/sites-enabled/development.onurozgultekin.com
nginx -t && systemctl reload nginx &&
  certbot --nginx -d development.onurozgultekin.com --redirect
```

### 4. İlk kurulum ve bağlantı

```bash
curl -sS -o /dev/null -w 'HTTP %{http_code}\n' \
  https://development.onurozgultekin.com/setup
docker exec codex-agent-platform cat /data/credentials/bootstrap-code
```

İlk kurulumda HTTP `200` beklenir. [Kurulum sayfasına](https://development.onurozgultekin.com/setup) sunucuda üretilen kodu girip yönetici hesabını ve en az 14 karakterlik şifresini oluşturun. Kurulum tamamlanınca kod geçersiz olur ve `/setup` giriş sayfasına yönlendirir.

**Codex ayarları → Cihaz koduyla giriş yap** üzerinden çıkan tek kullanımlık kodu OpenAI doğrulama sayfasına girin. ChatGPT hesabınızın güvenlik ayarlarında cihaz koduyla giriş etkin olmalıdır. Ardından **Bağlantıyı test et** ile hesabı ve model listesini kontrol edin. [Cihaz koduyla oturum açma](https://learn.chatgpt.com/docs/auth#login-on-headless-devices)

`Platform__LocalCodexLogin=true` web arayüzünde yeni hesap bağlama düğmesini açar. Bu seçenek kapalıyken App Server, diskteki kayıtlı hesabı kullanabilir. `Production` modu HTTPS ve güvenli çerezleri yönetir. Hesap oturumu `/data/codex` altında kalır ve normal yeniden başlatmalarda kullanılır.

Düğmeler tıklamaya yanıt vermiyorsa Blazor betiğinin yanıtını kontrol edin.

```bash
curl -sS -D - -o /dev/null \
  https://development.onurozgultekin.com/_framework/blazor.web.js
```

HTTP `200` ve JavaScript içerik türü beklenir. Betik `404` dönüyorsa framework dosyalarını içeren güncel imajı kullanın.

### Canlı container’da terminali etkinleştir

Ubuntu'da Docker'ın varsayılan güvenlik profilleri Bubblewrap'in iç Linux namespace'lerini engelleyebilir. Sohbet çalışırken çalışma alanı ve terminal kapalı görünüyorsa [Docker sandbox kurulum betiğini](deploy/docker-sandbox/enable.sh) çalışan sunucuda yürütün. Depoyu kopyalarken `deploy/docker-sandbox` klasörünü içeriğiyle birlikte taşıyın.

```bash
sudo bash deploy/docker-sandbox/enable.sh
```

Bu betik 2. adımdaki canlı kurulumu hedefler. Aynı imajı, uygulama ortam değişkenlerini, portu, ağı ve volume’ları kullanır. Geçiş sırasında kısa bir kesinti olur. HTTP, Bubblewrap ve çalışma alanı testlerinden biri başarısız olursa önceki container yeniden başlatılır. Başarılı geçişten sonra DevOps Engineer ile bir projede `pwd` çalıştırın ve terminal çıktısını kontrol edin.

Betiğin yüklediği AppArmor ve seccomp profilleri OpenAI’nin Docker içinde Bubblewrap çalıştırmak için yayımladığı yapılandırmaya dayanır. [OpenAI Docker sandbox belgeleri](https://github.com/openai/codex-security/blob/main/docker/README.md)

Terminali etkinleştirdiyseniz sonraki imaj güncellemelerinde 2. adımdaki `docker run` komutuna bu seçenekleri de ekleyin.

```bash
  --cap-drop=ALL \
  --security-opt=no-new-privileges:true \
  --security-opt=apparmor=codex-agent-platform-container \
  --security-opt=seccomp=/etc/docker/codex-agent-platform-seccomp.json \
```

### Yeni imaja geçiş

Sandbox betiğiyle kurulan canlı container’ı güncellemek için yeni imajı çekip aynı betiğe verin.

```bash
docker pull ghcr.io/unholywar/codexagentplatform:latest
sudo bash deploy/docker-sandbox/enable.sh --image ghcr.io/unholywar/codexagentplatform:latest
```

Betik mevcut disk bağlantılarını, uygulama ortam ayarlarını, portu ve sandbox profillerini kullanır. Yeni container kontrolleri geçemezse önceki container’ı yeniden başlatır.

Canlı container 2. adımdaki `docker run` komutuyla yönetiliyorsa önce imajı çekin. Mevcut container’ı durdurup geri dönüş için farklı bir ada alın, ardından canlı komutu mevcut bağlantılarla yeniden çalıştırın.

## Kalıcı veriler

- `codexagentplatform_platform-data` → `/data`. SQLite, sohbetler, hafıza notları, Codex oturumu ve uygulama verileri.
- `codexagentplatform_platform-workspaces` → `/workspaces`. Proje çalışma alanları.
- Yeniden dağıtırken aynı volume adlarını kullanın. Mevcut kurulum farklı bir Compose proje adı kullanıyorsa önce `docker volume ls` ile gerçek adları bulun. **Sistem** ekranından SQLite yedeği alabilirsiniz.

## Private Git ve SSH kaynakları

Uygulama Git erişim belirteçlerini ve SSH özel anahtarlarını şifreleyerek `/data/credentials` altında saklar. Vault anahtarını ayrı bir Docker volume’unda tutabilirsiniz. Çalışan canlı kurulumda ilk kez anahtar oluşturmak için aşağıdaki komutu Docker hostunda çalıştırın.

```bash
platform_image=$(docker inspect -f '{{.Image}}' codex-agent-platform)

docker run --rm --user 0:0 --entrypoint /bin/sh \
  -v codexagentplatform_vault-key:/vault "$platform_image" -ec '
    if [ ! -e /vault/master.key ]; then
      umask 077
      set -C
      node -e "process.stdout.write(require(\"node:crypto\").randomBytes(32).toString(\"base64\") + \"\\n\")" >/vault/master.key
    fi
    test -s /vault/master.key
    chown app:app /vault/master.key
    chmod 0400 /vault/master.key
  '
```

Mevcut container’ı durdurup geri dönüş için farklı bir ada alın. 2. adımdaki canlı `docker run` komutuna aşağıdaki seçenekleri ekleyin. Önceden Vault kullandıysanız mevcut anahtar dosyasını bağlamaya devam edin.

```bash
  --mount type=volume,src=codexagentplatform_vault-key,dst=/run/platform-vault,readonly \
  -e Platform__VaultKeyFile=/run/platform-vault/master.key \
```

Sonraki imaj güncellemelerinde aynı volume’u ve anahtar yolunu kullanın. Veri yedeğini geri yüklerken Vault kayıtlarını açmak için aynı anahtar gerekir.

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
