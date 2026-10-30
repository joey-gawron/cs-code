# CsCode shell integration for zsh (see .zshenv): a login shell's .zprofile, read from the user's ZDOTDIR.
if [[ -n "${CSCODE_USER_ZDOTDIR-}" ]]; then
    CSCODE_ZDOTDIR=$ZDOTDIR
    ZDOTDIR=$CSCODE_USER_ZDOTDIR
    if [[ "$ZDOTDIR" != "$CSCODE_ZDOTDIR" && -r "$ZDOTDIR/.zprofile" ]]; then
        builtin source "$ZDOTDIR/.zprofile"
    fi
    CSCODE_USER_ZDOTDIR=$ZDOTDIR
    ZDOTDIR=$CSCODE_ZDOTDIR
fi
