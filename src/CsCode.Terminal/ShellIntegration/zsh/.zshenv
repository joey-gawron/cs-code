# CsCode shell integration for zsh. CsCode starts an interactive zsh with ZDOTDIR pointing at this directory and the user's own ZDOTDIR
# (or HOME) in CSCODE_USER_ZDOTDIR: zsh reads these files, which read the user's files with the user's ZDOTDIR, and .zshrc then installs
# the OSC 633 hooks (cscode-integration.zsh) and hands ZDOTDIR back. The user's files are never changed.
if [[ -n "${CSCODE_USER_ZDOTDIR-}" ]]; then
    CSCODE_ZDOTDIR=$ZDOTDIR
    ZDOTDIR=$CSCODE_USER_ZDOTDIR
    if [[ "$ZDOTDIR" != "$CSCODE_ZDOTDIR" && -r "$ZDOTDIR/.zshenv" ]]; then
        builtin source "$ZDOTDIR/.zshenv"
    fi
    # the user's .zshenv may have moved ZDOTDIR: the other files come from there
    CSCODE_USER_ZDOTDIR=$ZDOTDIR
    ZDOTDIR=$CSCODE_ZDOTDIR
fi
