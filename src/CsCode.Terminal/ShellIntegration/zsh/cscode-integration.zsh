# CsCode shell integration for zsh: OSC 633 marks around the prompt (A, B), the command line (E) and its output (C), its exit code (D)
# and the working directory (P;Cwd), through precmd/preexec hooks. Sourced by this directory's .zshrc after the user's .zshrc.
if [[ -n "${__cscode_installed-}" ]] || [[ ! -o interactive ]]; then
    builtin return 0
fi
typeset -g __cscode_installed=1
typeset -g __cscode_nonce="${CSCODE_NONCE-}"
builtin unset CSCODE_NONCE CSCODE_INJECTION

# Directories CsCode put first on PATH (the selected Python environment's bin): a login shell's path_helper and the user's files may have moved
# them behind the system's, so they go first again (as VS Code does for its environment collections).
if [[ -n "${CSCODE_PATH_PREPEND-}" ]]; then
    builtin export PATH="$CSCODE_PATH_PREPEND:$PATH"
    builtin unset CSCODE_PATH_PREPEND
fi

typeset -g __cscode_status=0      # the last command's exit status, taken before any other precmd hook runs
typeset -g __cscode_executing=     # a command line ran since the last prompt
typeset -g __cscode_ps1=           # PS1 as wrapped last
typeset -g REPLY

# A value in an OSC 633 sequence: backslashes doubled, ';' and control characters as \xHH. Result in REPLY.
__cscode_escape() {
    builtin emulate -L zsh
    local s=$1 out= c i
    s=${s//\\/\\\\}
    s=${s//;/\\x3b}
    if [[ $s == *[[:cntrl:]]* ]]; then
        for (( i = 1; i <= ${#s}; i++ )); do
            c=${s[i]}
            if [[ $c == [[:cntrl:]] ]]; then builtin printf -v c '\\x%02x' "'$c"; fi
            out+=$c
        done
        s=$out
    fi
    REPLY=$s
}

# First precmd hook: the status of the command, before the user's hooks change $?.
__cscode_capture_status() {
    __cscode_status=$?
}

# Last precmd hook (after the user's, which may build PS1): D for the finished command, the directory, A and B around the prompt.
__cscode_precmd() {
    builtin emulate -L zsh
    if [[ -n $__cscode_executing ]]; then
        builtin printf '\e]633;D;%s\a' "$__cscode_status"
        __cscode_executing=
    fi
    __cscode_escape "$PWD"
    builtin printf '\e]633;P;Cwd=%s\a' "$REPLY"
    if [[ $PS1 != "$__cscode_ps1" ]]; then
        __cscode_ps1="%{"$'\e]633;A\a'"%}${PS1}%{"$'\e]633;B\a'"%}"
        PS1=$__cscode_ps1
    fi
}

# preexec gets the command line as typed: E with it, then C (its output starts here).
__cscode_preexec() {
    builtin emulate -L zsh
    __cscode_executing=1
    __cscode_escape "$1"
    builtin printf '\e]633;E;%s;%s\a\e]633;C\a' "$REPLY" "$__cscode_nonce"
}

builtin autoload -Uz add-zsh-hook
precmd_functions=(__cscode_capture_status $precmd_functions)
add-zsh-hook precmd __cscode_precmd
add-zsh-hook preexec __cscode_preexec
