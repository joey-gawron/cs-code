{
  description = "cs-zig: native Zig editor spike";
  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
  outputs = { nixpkgs, ... }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" ];
      forAll = f: nixpkgs.lib.genAttrs systems (s: f nixpkgs.legacyPackages.${s});
    in {
      devShells = forAll (pkgs: {
        default = pkgs.mkShell {
          packages = with pkgs; [
            zig pkg-config wayland-scanner wayland wayland-protocols libxkbcommon
            libGL libglvnd mesa freetype harfbuzz fontconfig
            xorg.libX11 xorg.libxcb xorg.libXcursor xorg.libXrandr xorg.libXi
          ];
          WAYLAND_PROTOCOLS_DIR = "${pkgs.wayland-protocols}/share/wayland-protocols";
          __EGL_VENDOR_LIBRARY_FILENAMES = "${pkgs.mesa}/share/glvnd/egl_vendor.d/50_mesa.json";
          LIBGL_DRIVERS_PATH = "${pkgs.mesa}/lib/dri";
          hardeningDisable = [ "all" ];
        };
      });
    };
}
