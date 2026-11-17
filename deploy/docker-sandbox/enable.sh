#!/usr/bin/env bash
set -euo pipefail

fail() {
  printf '%s\n' "$*" >&2
  exit 1
}

requested_image=''
if (( $# > 0 )); then
  [[ $# == 2 && $1 == --image && -n $2 ]] || fail 'Kullanım: bash enable.sh [--image İMAJ]'
  requested_image=$2
fi

[[ $EUID == 0 ]] || fail 'Sunucuda sudo bash enable.sh ile çalıştırın.'
for command in docker curl apparmor_parser; do
  command -v "$command" >/dev/null || fail "Gerekli komut bulunamadı: $command"
done

profile=codex-agent-platform-container
seccomp=/etc/docker/codex-agent-platform-seccomp.json
name=codex-agent-platform
network=codex-agent-backend
port=127.0.0.1:18090:8080
source_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
stamp=$(date -u +%Y%m%d%H%M%S)
backup="$name-before-sandbox-$stamp"
next="$name-sandbox-$stamp"

old=$(docker inspect --type container -f '{{.Id}}' "$name")
old_image=$(docker inspect -f '{{.Image}}' "$old")
image=$old_image
if [[ -n $requested_image ]]; then
  image=$(docker image inspect -f '{{.Id}}' -- "$requested_image") ||
    fail 'Seçilen imaj yerelde bulunamadı. Önce docker pull çalıştırın.'
  if [[ $image == "$old_image" ]]; then
    printf 'Container seçilen imajla zaten çalışıyor.\n'
    exit 0
  fi
  [[ $(docker image inspect -f '{{.Config.User}}' "$image") == app ]] ||
    fail 'Seçilen imaj beklenen app kullanıcısını içermiyor.'
fi
layout=$(docker inspect -f '{{.Config.User}} {{.HostConfig.NetworkMode}} {{json .HostConfig.PortBindings}}' "$old")
expected_layout="app $network {\"8080/tcp\":[{\"HostIp\":\"127.0.0.1\",\"HostPort\":\"18090\"}]}"
[[ $layout == "$expected_layout" ]] || fail 'Container kullanıcısı, ağı veya portu beklenen canlı kurulumla eşleşmedi.'

[[ $(docker inspect -f '{{.State.Running}}' "$old") == true ]] || fail 'Mevcut container çalışmıyor.'
[[ $(docker inspect -f '{{len .HostConfig.Tmpfs}}' "$old") == 0 ]] ||
  fail 'Container özel tmpfs bağlantıları içeriyor. Otomatik geçiş durduruldu.'

mount_layout() {
  docker inspect -f '{{range .Mounts}}{{printf "%q %q %q %q %q %t %q\n" .Destination .Type .Name .Source .Driver .RW .Propagation}}{{end}}' "$1" |
    LC_ALL=C sort
}

writable_mounts=$(docker inspect -f '{{range .Mounts}}{{if and .RW (or (eq .Type "volume") (eq .Type "bind"))}}{{println .Destination}}{{end}}{{end}}' "$old")
for target in /data /workspaces; do
  grep -Fxq "$target" <<< "$writable_mounts" || fail "$target için yazılabilir ve kalıcı bir disk bağlantısı bulunamadı."
done
mounts=$(mount_layout "$old")

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
  if ! is_old_image_default "$variable"; then
    env_args+=(--env "$variable")
  fi
done < <(docker inspect -f "$env_format" "$old")

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
  --volumes-from "$old" "${env_args[@]}" "$image")

[[ $(mount_layout "$replacement") == "$mounts" ]] ||
  fail 'Yeni container aynı disk bağlantılarını devralamadı. Çalışan container durdurulmadan geçiş iptal edildi.'
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

docker exec "$name" bwrap --unshare-user --unshare-ipc --unshare-pid \
  --unshare-net --ro-bind / / /bin/true

docker exec -i -e CODEX_HOME=/data/codex "$name" /bin/sh -s <<'CHECK'
set -eu
test -r /data/codex/config.toml
probe=$(mktemp -d /workspaces/sandbox-check.XXXXXX)
trap 'rm -f -- "$probe/.env" "$probe/probe.txt"; rmdir -- "$probe"' EXIT
printf 'probe\n' >"$probe/.env"
codex sandbox -P platform_work -C "$probe" -- /bin/sh -ec '
  if cat /data/codex/config.toml >/dev/null 2>&1 || cat .env >/dev/null 2>&1; then
    echo "Sandbox dosya erişimi kontrolü başarısız." >&2
    exit 1
  fi
  printf "ok\n" >probe.txt
'
test "$(cat "$probe/probe.txt")" = ok
CHECK

trap - EXIT INT TERM
printf 'Uygulama, Bubblewrap ve çalışma alanı testleri geçti.\n'
printf 'Önceki container durduruldu ve %s adıyla saklandı.\n' "$backup"
