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

### Yeni imaja geçiş

Yeni sürüm yayımlandığında `docker pull ghcr.io/unholywar/codexagentplatform:latest` çalıştırın. Mevcut container’ı durdurup geri dönüş için farklı bir ada alın, ardından 2. adımdaki canlı komutu aynı volume adlarıyla yeniden çalıştırın. Nginx, aynı yerel porta yönlendirmeye devam eder.

## Kalıcı veriler

- `codexagentplatform_platform-data` → `/data`. SQLite, sohbetler, hafıza notları, Codex oturumu ve uygulama verileri.
- `codexagentplatform_platform-workspaces` → `/workspaces`. Proje çalışma alanları.
- Yeniden dağıtırken aynı volume adlarını kullanın. Mevcut kurulum farklı bir Compose proje adı kullanıyorsa önce `docker volume ls` ile gerçek adları bulun. **Sistem** ekranından SQLite yedeği alabilirsiniz.
- Git veya SSH erişim bilgileri saklanacaksa 32 baytlık base64 Vault anahtarını salt okunur dosya olarak bağlayıp container içindeki yolunu `Platform__VaultKeyFile` ile verin. Geri yüklemede aynı anahtarı kullanın.

Depodaki [Docker Compose dosyası](docker-compose.yml), TLS’i uygulamanın karşıladığı alternatif loopback kurulumudur. Bu seçenekte PFX, sertifika parolası ve Vault anahtarı gerekir.

## Yerel geliştirme

Depo kökünden çalıştırın. Visual Studio’da `CodexAgentPlatform.slnx` dosyasını ve `https` profilini seçebilirsiniz.

```powershell
dotnet dev-certs https --trust
dotnet run --project CodexAgentPlatform --launch-profile https
dotnet test CodexAgentPlatform.slnx
```

Yerel adres [https://localhost:7213](https://localhost:7213). Codex CLI’nin kurulu olduğunu `codex --version` ile kontrol edin. Özel CLI yolu için `Platform__CodexExecutable` kullanılır. Yerel terminal işlemleri için [Codex sandbox gereksinimlerine](https://learn.chatgpt.com/docs/permissions#how-enforcement-works) bakın.
