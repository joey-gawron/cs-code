# CsCode shell integration for bash: OSC 633 marks around the prompt, the command line, its output and its exit code, and the working
# directory, so the terminal can decorate commands, jump between them (Ctrl+Up/Down) and read the last command's output.
#
# CsCode starts an interactive bash as `bash --init-file <this file>`: bash then reads this file instead of ~/.bashrc (a system-wide rc
# file such as Debian's /etc/bash.bashrc it still reads itself, first), so this file first reads what bash would have read: ~/.bashrc, or
# the profile's own --rcfile (CSCODE_BASH_RCFILE); for a login shell (CSCODE_SHELL_LOGIN: --init-file does not apply to one, so CsCode
# starts an interactive shell instead) /etc/profile and the first of ~/.bash_profile, ~/.bash_login, ~/.profile, unless --noprofile.
# A profile with --norc keeps its arguments and bash reads nothing; this file is then sourced by the first prompt (PROMPT_COMMAND,
# CSCODE_BASH_BOOTSTRAP). The user's files are never changed. Turn it off with terminal.integrated.shellIntegration.enabled = false.

if [ -n "${__cscode_installed:-}" ]; then builtin return 0; fi

__cscode_bootstrap=${CSCODE_BASH_BOOTSTRAP:-}
__cscode_login=${CSCODE_SHELL_LOGIN:-}
__cscode_noprofile=${CSCODE_BASH_NOPROFILE:-}
__cscode_rcfile=${CSCODE_BASH_RCFILE:-}
__cscode_nonce=${CSCODE_NONCE:-}
# not inherited by programs started from this shell (a nested bash is a plain bash again)
builtin unset CSCODE_BASH_BOOTSTRAP CSCODE_SHELL_LOGIN CSCODE_BASH_NOPROFILE CSCODE_BASH_RCFILE CSCODE_NONCE CSCODE_INJECTION

if [ -n "$__cscode_bootstrap" ]; then
    # sourced by the first prompt's PROMPT_COMMAND: bash has done its own startup; the variable must not reach child shells
    builtin export -n PROMPT_COMMAND
    PROMPT_COMMAND=
elif [ -n "$__cscode_login" ]; then
    if [ -z "$__cscode_noprofile" ]; then
        if [ -r /etc/profile ]; then . /etc/profile; fi
        if [ -r ~/.bash_profile ]; then . ~/.bash_profile
        elif [ -r ~/.bash_login ]; then . ~/.bash_login
        elif [ -r ~/.profile ]; then . ~/.profile
        fi
    fi
elif [ -n "$__cscode_rcfile" ]; then
    if [ -r "$__cscode_rcfile" ]; then . "$__cscode_rcfile"; fi
elif [ -r ~/.bashrc ]; then
    . ~/.bashrc
fi
builtin unset __cscode_login __cscode_noprofile __cscode_rcfile

# The user's files may have installed it already (or this is not an interactive shell after all).
if [ -n "${__cscode_installed:-}" ] || [[ $- != *i* ]]; then builtin return 0; fi
__cscode_installed=1

# A value in an OSC 633 sequence: backslashes doubled, ';' and control characters as \xHH, so it can never end the sequence early.
# The result is left in __cscode_escaped (no subshell).
__cscode_escape() {
    builtin local s="$1" out="" c i   # quoted: "builtin local" is an ordinary command, its words are split
    s=${s//\\/\\\\}
    s=${s//;/\\x3b}
    if [[ $s == *[[:cntrl:]]* ]]; then
        for (( i = 0; i < ${#s}; i++ )); do
            c=${s:i:1}
            if [[ $c == [[:cntrl:]] ]]; then builtin printf -v c '\\x%02x' "'$c"; fi
            out+=$c
        done
        s=$out
    fi
    __cscode_escaped=$s
}

__cscode_executing=     # a command line ran since the last prompt (C was sent): the next prompt reports D with its status
__cscode_in_prompt=     # set once the prompt is drawn: the DEBUG trap's next hit is the command line the user entered
__cscode_next_hist=     # HISTCMD at the prompt: the number history gives the next command line if it saves it
__cscode_ps1=           # PS1 as this file last wrapped it
__cscode_user_debug=    # a DEBUG trap the user's files had set (still run)

# First of the prompt commands: report how the command ended, and the directory.
__cscode_precmd() {
    builtin local status="$?"
    __cscode_in_prompt=
    if [ -n "$__cscode_executing" ]; then
        builtin printf '\e]633;D;%s\a' "$status"
        __cscode_executing=
    fi
    __cscode_escape "$PWD"
    builtin printf '\e]633;P;Cwd=%s\a' "$__cscode_escaped"
    builtin return $status   # the user's prompt commands still see the command's $?
}

# Last of the prompt commands (after the user's, which may set PS1 themselves): A before the prompt, B after it.
__cscode_prompt() {
    builtin local status="$?"
    if [ "$PS1" != "$__cscode_ps1" ]; then
        __cscode_ps1="\[\e]633;A\a\]$PS1\[\e]633;B\a\]"
        PS1=$__cscode_ps1
    fi
    __cscode_next_hist=$HISTCMD
    __cscode_in_prompt=1
    builtin return $status
}

# The command line as history saved it (the whole line, not just its first simple command); when history did not save it
# (HISTCONTROL=ignorespace, history off), the first simple command bash is about to run.
__cscode_command_line() {
    builtin local entry
    if [[ -o history && -n "$__cscode_next_hist" && "$HISTCMD" == "$__cscode_next_hist" ]] \
        && entry=$(HISTTIMEFORMAT='' builtin history 1) && [[ $entry =~ ^[[:space:]]*[0-9]+[*]?[[:space:]]+(.*)$ ]]; then
        __cscode_escape "${BASH_REMATCH[1]}"
    else
        __cscode_escape "$BASH_COMMAND"
    fi
}

# The DEBUG trap: its first hit after the prompt is the command the user entered: E (the line) and C (output starts here).
__cscode_debug() {
    if [ -n "$__cscode_in_prompt" ]; then
        case "$BASH_COMMAND" in
            __cscode_*) ;;
            *)
                __cscode_in_prompt=
                __cscode_executing=1
                __cscode_command_line
                builtin printf '\e]633;E;%s;%s\a\e]633;C\a' "$__cscode_escaped" "$__cscode_nonce"
                ;;
        esac
    fi
    if [ -n "$__cscode_user_debug" ]; then builtin eval "$__cscode_user_debug"; fi
    : "$1"   # leaves $_ as it was before the trap
}

if [[ -n "${bash_preexec_imported:-}${__bp_imported:-}" ]]; then
    # bash-preexec owns PROMPT_COMMAND and the DEBUG trap: join its hook lists instead
    __cscode_bp_preexec() {
        __cscode_executing=1
        __cscode_escape "$1"
        builtin printf '\e]633;E;%s;%s\a\e]633;C\a' "$__cscode_escaped" "$__cscode_nonce"
    }
    precmd_functions=(__cscode_precmd "${precmd_functions[@]}" __cscode_prompt)
    preexec_functions+=(__cscode_bp_preexec)
else
    if [[ "$(builtin declare -p PROMPT_COMMAND 2>/dev/null)" == "declare -a"* ]]; then
        PROMPT_COMMAND=(__cscode_precmd "${PROMPT_COMMAND[@]}" __cscode_prompt)   # bash 5.1's array form
    else
        # newlines, not ';': a user's PROMPT_COMMAND ending in ';' must not become ';;'
        PROMPT_COMMAND=$'__cscode_precmd\n'"${PROMPT_COMMAND:-}"$'\n__cscode_prompt'
    fi
    __cscode_trap=$(builtin trap -p DEBUG)
    if [ -n "$__cscode_trap" ]; then
        builtin eval "__cscode_trap=( $__cscode_trap )"   # trap -- 'command' DEBUG
        __cscode_user_debug=${__cscode_trap[2]}
    fi
    builtin unset __cscode_trap
    builtin trap '__cscode_debug "$_"' DEBUG
fi

# Sourced by the first prompt (--norc): that prompt's commands are already running, so mark it now. In a function: from here on every
# top-level command would hit the DEBUG trap and look like the user's first command.
__cscode_finish_install() {
    if [ -n "$__cscode_bootstrap" ]; then
        __cscode_precmd
        __cscode_prompt
    fi
    builtin unset __cscode_bootstrap
    builtin unset -f __cscode_finish_install
}
__cscode_finish_install
