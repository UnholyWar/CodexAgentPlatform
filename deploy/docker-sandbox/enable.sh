#!/usr/bin/env bash
set -euo pipefail

fail() {
  printf '%s\n' "$*" >&2
  exit 1
}

requested_image=''
cleanup_requested=false
vault_requested=false
updater_requested=false
usage='Kullanım: bash enable.sh [--image İMAJ] [--enable-vault] [--cleanup-old-images] [--enable-updater]'
while (( $# > 0 )); do
  case "$1" in
    --image)
      [[ $# -ge 2 && -z $requested_image && -n $2 && $2 != --* ]] || fail "$usage"
      requested_image=$2
      shift 2 ;;
    --cleanup-old-images) cleanup_requested=true; shift ;;
    --enable-vault) vault_requested=true; shift ;;
    --enable-updater) updater_requested=true; shift ;;
    *) fail "$usage" ;;
  esac
done

[[ $EUID == 0 ]] || fail 'Sunucuda sudo bash enable.sh ile çalıştırın.'
command -v docker >/dev/null || fail 'Gerekli komut bulunamadı: docker'

profile=codex-agent-platform-container
seccomp=/etc/docker/codex-agent-platform-seccomp.json
name=codex-agent-platform
network=codex-agent-backend
port=127.0.0.1:18090:8080
source_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
stamp=$(date -u +%Y%m%d%H%M%S)
backup="$name-before-sandbox-$stamp"
next="$name-sandbox-$stamp"

cleanup_old_images() {
  local keep="${1:-}" backups candidate ref candidate_id candidate_image metadata
  local images image_id references tags status=0
  local source='https://github.com/UnholyWar/CodexAgentPlatform'

  backups=$(docker container ls -a --filter status=exited \
    --filter "name=$name-before-sandbox-" --format '{{.Names}}' | LC_ALL=C sort -r) || return 1
  while IFS= read -r candidate; do
    [[ $candidate =~ ^codex-agent-platform-before-sandbox-[0-9]{14}$ ]] || continue
    if [[ -z $keep ]]; then keep=$candidate; fi
    [[ $candidate != "$keep" ]] || continue
    ref=$(docker container inspect -f '{{.Id}} {{.Image}}' "$candidate") || { status=1; continue; }
    read -r candidate_id candidate_image <<< "$ref"
    metadata=$(docker image inspect -f '{{len .RepoTags}} {{index .Config.Labels "org.opencontainers.image.source"}}' "$candidate_image") ||
      { status=1; continue; }
    [[ $metadata == "0 $source" ]] || continue
    docker container rm "$candidate_id" || status=1
  done <<< "$backups"

  images=$(docker image ls --no-trunc -q --filter dangling=true \
    --filter "label=org.opencontainers.image.source=$source") || return 1
  while IFS= read -r image_id; do
    [[ -n $image_id ]] || continue
    references=$(docker container ls -aq --filter "ancestor=$image_id") || { status=1; continue; }
    [[ -z $references ]] || continue
    tags=$(docker image inspect -f '{{len .RepoTags}}' "$image_id") || { status=1; continue; }
    [[ $tags == 0 ]] || continue
    docker image rm --no-prune "$image_id" || status=1
  done <<< "$images"
  return "$status"
}

old=$(docker inspect --type container -f '{{.Id}}' "$name")
[[ $(docker inspect -f '{{.State.Running}}' "$old") == true ]] || fail 'Mevcut container çalışmıyor.'
if [[ $cleanup_requested == true && -z $requested_image && $vault_requested == false && $updater_requested == false ]]; then
  cleanup_old_images
  exit 0
fi

old_image=$(docker inspect -f '{{.Image}}' "$old")
image=$old_image
vault_volume=codexagentplatform_vault-key
vault_target=/run/platform-vault
vault_key_path=$vault_target/master.key
vault_mount_format='{{range .Mounts}}{{if eq .Destination "/run/platform-vault"}}{{.Type}} {{.Name}} {{.RW}}{{end}}{{end}}'
vault_key_check='
const key = require("node:fs").readFileSync(process.argv[1], "utf8").replace(/\s/g, "");
if (!/^[A-Za-z0-9+/]{43}=$/.test(key)) process.exit(1);
'
configure_vault=false
if [[ $vault_requested == true ]]; then
  current_vault_key=$(docker exec "$old" /bin/sh -c 'printf %s "${Platform__VaultKeyFile:-}"')
  if [[ -n $current_vault_key ]]; then
    docker exec "$old" node -e "$vault_key_check" "$current_vault_key" ||
      fail 'Mevcut Vault anahtarı okunamadı veya geçersiz. Özgün anahtar dosyasını kontrol edin.'
    if [[ -z $requested_image && $updater_requested == false ]]; then
      printf 'Vault zaten etkin. Mevcut anahtar kullanılıyor.\n'
      if [[ $cleanup_requested == true ]]; then cleanup_old_images; fi
      exit 0
    fi
  else
    configure_vault=true
  fi
fi
if [[ -n $requested_image ]]; then
  image=$(docker image inspect -f '{{.Id}}' -- "$requested_image") ||
    fail 'Seçilen imaj yerelde bulunamadı. Önce docker pull çalıştırın.'
  if [[ $image == "$old_image" && $configure_vault == false && $updater_requested == false ]]; then
    printf 'Container seçilen imajla zaten çalışıyor.\n'
    if [[ $cleanup_requested == true ]]; then cleanup_old_images; fi
    exit 0
  fi
  [[ $(docker image inspect -f '{{.Config.User}}' "$image") == app ]] ||
    fail 'Seçilen imaj beklenen app kullanıcısını içermiyor.'
fi
layout=$(docker inspect -f '{{.Config.User}} {{.HostConfig.NetworkMode}} {{json .HostConfig.PortBindings}}' "$old")
expected_layout="app $network {\"8080/tcp\":[{\"HostIp\":\"127.0.0.1\",\"HostPort\":\"18090\"}]}"
[[ $layout == "$expected_layout" ]] || fail 'Container kullanıcısı, ağı veya portu beklenen canlı kurulumla eşleşmedi.'

[[ $(docker inspect -f '{{len .HostConfig.Tmpfs}}' "$old") == 0 ]] ||
  fail 'Container özel tmpfs bağlantıları içeriyor. Otomatik geçiş durduruldu.'
for command in curl apparmor_parser; do
  command -v "$command" >/dev/null || fail "Gerekli komut bulunamadı: $command"
done

mount_layout() {
  docker inspect -f '{{range .Mounts}}{{printf "%q %q %q %q %q %t %q\n" .Destination .Type .Name .Source .Driver .RW .Propagation}}{{end}}' "$1" |
    LC_ALL=C sort
}

updater_dir=/var/lib/codex-platform-updater
updater_mount_args=()
if [[ $updater_requested == true ]]; then
  updater_mount=$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/run/platform-update"}}{{.Type}} {{.Source}} {{.RW}}{{end}}{{end}}' "$old")
  if [[ -z $updater_mount ]]; then
    updater_mount_args+=(--mount "type=bind,src=$updater_dir,dst=/run/platform-update,readonly")
  elif [[ $updater_mount != "bind $updater_dir false" ]]; then
    fail 'Beklenmeyen güncelleyici bağlantısı.'
  fi
  command -v python3 >/dev/null || fail 'Güncelleyici için python3 gerekli.'
  install -d -o root -g root -m 0755 "$updater_dir" /opt/codex-platform-updater
  install -m 0644 "$source_dir"/{enable.sh,updater.py,codex-agent-platform.apparmor,seccomp.json} /opt/codex-platform-updater/
  install -m 0644 "$source_dir/codex-platform-updater.service" /etc/systemd/system/
  systemctl daemon-reload
  systemctl enable --now codex-platform-updater.service
  for attempt in {1..10}; do
    [[ ! -S $updater_dir/update.sock ]] || break
    sleep 1
  done
  [[ -S $updater_dir/update.sock ]] || fail 'Güncelleyici soketi açılmadı.'
fi

original_mount_layout() {
  local layout
  layout=$(mount_layout "$1") || return 1
  if (( ${#vault_mount_args[@]} )); then
    layout=$(printf '%s\n' "$layout" | grep -v '^"/run/platform-vault" ')
  fi
  if (( ${#updater_mount_args[@]} )); then
    layout=$(printf '%s\n' "$layout" | grep -v '^"/run/platform-update" ')
  fi
  printf '%s\n' "$layout"
}

writable_mounts=$(docker inspect -f '{{range .Mounts}}{{if and .RW (or (eq .Type "volume") (eq .Type "bind"))}}{{println .Destination}}{{end}}{{end}}' "$old")
for target in /data /workspaces; do
  grep -Fxq "$target" <<< "$writable_mounts" || fail "$target için yazılabilir ve kalıcı bir disk bağlantısı bulunamadı."
done
mounts=$(mount_layout "$old")

vault_mount_args=()
if [[ $configure_vault == true ]]; then
  mounted_vault=$(docker inspect -f "$vault_mount_format" "$old")
  if [[ -z $mounted_vault ]]; then
    vault_mount_args+=(--mount "type=volume,src=$vault_volume,dst=$vault_target,readonly")
  elif [[ $mounted_vault != "volume $vault_volume false" ]]; then
    fail 'Vault hedefindeki mevcut disk bağlantısı beklenen salt okunur volume ile eşleşmiyor.'
  fi

  generate_key=false
  if docker exec "$old" /bin/sh -ec '
    for entry in "${Platform__Data:-/data}/credentials/"*.vault; do
      [ ! -e "$entry" ] || exit 1
    done
  '; then
    generate_key=true
  fi
  docker run --pull never --rm --network none --user 0:0 --entrypoint /bin/sh \
    --mount "type=volume,src=$vault_volume,dst=/vault" "$image" -ec '
    key=/vault/master.key
    if [ ! -e "$key" ]; then
      if [ "$1" != true ]; then
        echo "Vault kayıtları varsa özgün anahtar gerekir." >&2
        exit 1
      fi
      umask 077
      temporary_key=$(mktemp /vault/.master-key.XXXXXX)
      trap "rm -f -- \"$temporary_key\"" EXIT
      node -e "process.stdout.write(require(\"node:crypto\").randomBytes(32).toString(\"base64\"))" >"$temporary_key"
      chown app:app "$temporary_key"
      chmod 0400 "$temporary_key"
      ln "$temporary_key" "$key" || test -f "$key"
    fi
    test -f "$key" && test ! -L "$key"
    node -e "$2" "$key"
  ' setup-vault "$generate_key" "$vault_key_check" || fail 'Vault anahtarı hazırlanamadı.'
  docker run --pull never --rm --network none --user app --entrypoint node \
    --mount "type=volume,src=$vault_volume,dst=/vault,readonly" \
    "$image" -e "$vault_key_check" /vault/master.key || fail 'Uygulama kullanıcısı Vault anahtarını okuyamadı.'
fi

env_format='{{range .Config.Env}}{{printf "%s\x00" .}}{{end}}'
image_defaults=()
if [[ $image != "$old_image" ]]; then
  mapfile -d '' -t image_defaults < <(docker image inspect -f "$env_format" "$old_image")
fi
is_old_image_default() {
  local setting
  for setting in "${image_defaults[@]}"; do
    if [[ $1 == "$setting" ]]; then return 0; fi
  done
  return 1
}

env_args=()
while IFS= read -r -d '' variable; do
  if [[ $configure_vault == true && $variable == Platform__VaultKeyFile=* ]]; then
    continue
  fi
  if ! is_old_image_default "$variable"; then
    env_args+=(--env "$variable")
  fi
done < <(docker inspect -f "$env_format" "$old")
if [[ $configure_vault == true ]]; then
  env_args+=(--env "Platform__VaultKeyFile=$vault_key_path")
fi

apparmor_file=/etc/apparmor.d/$profile
apparmor_parser -Q -W "$source_dir/codex-agent-platform.apparmor"
install -m 0644 "$source_dir/codex-agent-platform.apparmor" "$apparmor_file"
apparmor_parser -r -W "$apparmor_file"
install -D -m 0644 "$source_dir/seccomp.json" "$seccomp"

replacement=''
switched=false
restore() {
  local status=$?
  trap - EXIT
  if (( status != 0 )) && [[ -n $replacement ]]; then
    docker rm -f "$replacement" >/dev/null || true
  fi
  if (( status != 0 )) && [[ $switched == true ]]; then
    if [[ $(docker inspect -f '{{.Name}}' "$old") != "/$name" ]]; then
      docker rename "$old" "$name" || true
    fi
    if docker start "$old" >/dev/null; then
      printf 'Geçiş başarısız. Önceki container yeniden başlatıldı.\n' >&2
    else
      printf 'Önceki container başlatılamadı. Container kimliği %s\n' "$old" >&2
    fi
  fi
  exit "$status"
}
trap restore EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

replacement=$(docker create --pull never --name "$next" \
  --init --restart unless-stopped --network "$network" -p "$port" \
  --cap-drop=ALL --security-opt=no-new-privileges:true \
  --security-opt="apparmor=$profile" --security-opt="seccomp=$seccomp" \
  --volumes-from "$old" "${vault_mount_args[@]}" "${updater_mount_args[@]}" "${env_args[@]}" "$image")

[[ $(docker inspect -f '{{.Image}}' "$replacement") == "$image" ]] ||
  fail 'Docker seçilen imajla container oluşturmadı. Çalışan container durdurulmadan geçiş iptal edildi.'
[[ $(original_mount_layout "$replacement") == "$mounts" ]] ||
  fail 'Yeni container aynı disk bağlantılarını devralamadı. Çalışan container durdurulmadan geçiş iptal edildi.'
if [[ $configure_vault == true ]]; then
  [[ $(docker inspect -f "$vault_mount_format" "$replacement") == "volume $vault_volume false" ]] ||
    fail 'Yeni container salt okunur Vault volume ile oluşturulamadı.'
fi
switched=true
docker stop -t 45 "$old" >/dev/null
docker rename "$old" "$backup"
docker rename "$replacement" "$name"
docker start "$replacement" >/dev/null

for attempt in {1..20}; do
  if curl --max-time 2 -fsS http://127.0.0.1:18090/ -o /dev/null 2>/dev/null; then
    break
  fi
  [[ $attempt != 20 ]] || fail 'Uygulama HTTP kontrolünü geçemedi.'
  sleep 2
done

if [[ $configure_vault == true ]]; then
  docker exec "$name" node -e "$vault_key_check" "$vault_key_path" ||
    fail 'Yeni container Vault anahtarını okuyamadı.'
fi

if [[ $updater_requested == true || $(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/run/platform-update"}}yes{{end}}{{end}}' "$name") == yes ]]; then
  docker exec "$name" node -e '
    const socket = require("node:net").createConnection("/run/platform-update/update.sock");
    socket.setTimeout(3000, () => socket.destroy(new Error("Updater status timed out")));
    socket.once("connect", () => socket.write("status\n"));
    let body = "";
    socket.on("data", chunk => body += chunk);
    socket.once("end", () => {
      if (!["idle", "running", "installed", "failed"].includes(JSON.parse(body).state)) process.exitCode = 1;
    });
  ' || fail 'Yeni container güncelleyiciye erişemedi.'
fi

docker exec "$name" bwrap --unshare-user --unshare-ipc --unshare-pid \
  --unshare-net --ro-bind / / /bin/true

docker exec -i -e CODEX_HOME=/data/codex "$name" /bin/sh -s <<'CHECK'
set -eu
test -r /data/codex/config.toml
probe=$(mktemp -d /workspaces/sandbox-check.XXXXXX)
trap 'rm -f -- "$probe/.env" "$probe/probe.txt"; rmdir -- "$probe"' EXIT
printf 'probe\n' >"$probe/.env"
vault_key=${Platform__VaultKeyFile:-}
codex sandbox -P platform_work -C "$probe" -- /bin/sh -ec '
  if [ -S /run/platform-update/update.sock ] || cat /data/codex/config.toml >/dev/null 2>&1 || cat .env >/dev/null 2>&1 ||
    { [ -n "$1" ] && cat -- "$1" >/dev/null 2>&1; }; then
    echo "Sandbox dosya erişimi kontrolü başarısız." >&2
    exit 1
  fi
  printf "ok\n" >probe.txt
' sandbox-check "$vault_key"
test "$(cat "$probe/probe.txt")" = ok
CHECK

trap - EXIT INT TERM
printf 'Uygulama, Bubblewrap ve çalışma alanı testleri geçti.\n'
if [[ $configure_vault == true ]]; then printf 'Vault etkinleştirildi.\n'; fi
printf 'Çalışan imaj: %s\n' "$image"
printf 'Önceki container durduruldu ve %s adıyla saklandı.\n' "$backup"
if [[ $cleanup_requested == true ]]; then
  cleanup_old_images "$backup" || printf 'İmaj temizliğinde hata oluştu. Yeni container çalışmaya devam ediyor.\n' >&2
fi
