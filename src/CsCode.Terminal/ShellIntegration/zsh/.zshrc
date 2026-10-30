# CsCode shell integration for zsh (see .zshenv): the user's .zshrc, then the OSC 633 hooks. From here on ZDOTDIR is the user's again,
# so a login shell's .zlogin and every zsh started from this one read the user's own files.
if [[ -n "${CSCODE_USER_ZDOTDIR-}" ]]; then
    CSCODE_ZDOTDIR=$ZDOTDIR
    ZDOTDIR=$CSCODE_USER_ZDOTDIR
    if [[ "$ZDOTDIR" != "$CSCODE_ZDOTDIR" && -r "$ZDOTDIR/.zshrc" ]]; then
        builtin source "$ZDOTDIR/.zshrc"
    fi
    if [[ -o interactive && -r "$CSCODE_ZDOTDIR/cscode-integration.zsh" ]]; then
        builtin source "$CSCODE_ZDOTDIR/cscode-integration.zsh"
    fi
    # the user had no ZDOTDIR: leave none (zsh then uses HOME, as before)
    if [[ -n "${CSCODE_ZDOTDIR_UNSET-}" && "$ZDOTDIR" == "$CSCODE_USER_ZDOTDIR" ]]; then
        builtin unset ZDOTDIR
    fi
    builtin unset CSCODE_ZDOTDIR CSCODE_USER_ZDOTDIR CSCODE_ZDOTDIR_UNSET
fi
