namespace CodexAgentPlatform.Components.Pages;

public partial class Update
{
    private static string Instructions(string version) => $$$"""
        (
          set -euo pipefail
          image=ghcr.io/unholywar/codexagentplatform:{{{version}}}
          docker pull "$image"
          id=$(docker image inspect -f '{{.Id}}' "$image")
          test "$(docker image inspect -f '{{index .Config.Labels "org.opencontainers.image.version"}}' "$id")" = '{{{version}}}'
          dir=$(mktemp -d)
          trap 'rm -rf -- "$dir"' EXIT
          tar -xzf /root/codex-docker-vault.tar.gz -C "$dir"
          bash "$dir/docker-sandbox/enable.sh" --image "$id" --enable-vault
          test "$(docker inspect -f '{{.State.Running}} {{.Image}}' codex-agent-platform)" = "true $id"
          case "$(curl --max-time 5 -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:18090/)" in
            200|302) echo 'Güncelleme doğrulandı.' ;;
            *) exit 1 ;;
          esac
        )
        """;
}
