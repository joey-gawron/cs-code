namespace CsCode.Testing.Providers.Python;

/// <summary>
/// The helpers the Python provider hands the interpreter: a pytest plugin (<c>-p cscode_pytest</c>) and a unittest discovery / runner script, written
/// to a fresh temp directory per discovery or run and put on <c>PYTHONPATH</c>. Each prints one line per event, <c>@@cscode {json}</c>, among the
/// tests' own output; everything else is the tests' output.
/// </summary>
internal static class PythonTestScripts
{
    public const string Marker = "@@cscode ";
    public const string PytestPlugin = "cscode_pytest";
    public const string UnittestScript = "cscode_unittest.py";

    /// <summary>
    /// The pytest plugin: the rootdir, every collected item (node id, absolute file, 0-based line, class, parametrization) and the start of each
    /// test while running. Results come from <c>--junitxml</c>.
    /// </summary>
    public const string PytestPluginSource = """
        import json


        def _emit(event, **data):
            data["event"] = event
            print("@@cscode " + json.dumps(data), flush=True)


        def pytest_sessionstart(session):
            _emit("rootdir", path=str(session.config.rootpath))


        def pytest_collection_finish(session):
            for item in session.items:
                try:
                    _, line, _ = item.location
                except Exception:
                    line = None
                cls = getattr(item, "cls", None)
                spec = getattr(item, "callspec", None)
                _emit("item", id=item.nodeid, file=str(getattr(item, "path", "") or ""), line=line,
                      cls=cls.__name__ if cls is not None else None, name=getattr(item, "originalname", None) or item.name,
                      param=spec.id if spec is not None else None)


        def pytest_runtest_logstart(nodeid, location):
            _emit("start", id=nodeid)
        """;

    /// <summary>
    /// unittest: <c>discover &lt;start&gt; &lt;pattern&gt; &lt;top&gt;</c> lists the tests (id, file, 0-based line, class, method; import errors as
    /// <c>error</c>); <c>run &lt;top&gt; &lt;id&gt;…</c> runs them (all when no id) and reports each start and result with its duration and traceback.
    /// </summary>
    public const string UnittestSource = """
        import inspect
        import json
        import os
        import sys
        import time
        import traceback
        import unittest


        def emit(event, **data):
            data["event"] = event
            sys.__stdout__.write("@@cscode " + json.dumps(data) + "\n")
            sys.__stdout__.flush()


        def walk(suite):
            for test in suite:
                if isinstance(test, unittest.TestSuite):
                    yield from walk(test)
                else:
                    yield test


        def location(test):
            method = getattr(test, getattr(test, "_testMethodName", ""), None)
            try:
                func = inspect.unwrap(method)
                return inspect.getsourcefile(func), inspect.getsourcelines(func)[1] - 1
            except Exception:
                return None, None


        def discover(start, pattern, top):
            loader = unittest.TestLoader()
            suite = loader.discover(start, pattern=pattern, top_level_dir=top or None)
            for error in loader.errors:
                emit("error", message=error)
            for test in walk(suite):
                tid = test.id()
                if type(test).__name__ == "_FailedTest":
                    emit("error", message=tid + ": " + str(getattr(test, "_exception", "")))
                    continue
                file, line = location(test)
                emit("item", id=tid, file=file, line=line, cls=type(test).__name__, name=getattr(test, "_testMethodName", tid))


        class Result(unittest.TextTestResult):
            def startTest(self, test):
                self._started = time.perf_counter()
                emit("start", id=test.id())
                super().startTest(test)

            def _done(self, test, outcome, err=None, reason=None):
                message = None
                trace = None
                if err is not None:
                    message = "".join(traceback.format_exception_only(err[0], err[1])).strip()
                    trace = "".join(traceback.format_exception(*err))
                emit("result", id=test.id(), outcome=outcome, duration=time.perf_counter() - getattr(self, "_started", time.perf_counter()),
                     message=message or reason, traceback=trace)

            def addSuccess(self, test):
                super().addSuccess(test)
                self._done(test, "passed")

            def addFailure(self, test, err):
                super().addFailure(test, err)
                self._done(test, "failed", err)

            def addError(self, test, err):
                super().addError(test, err)
                self._done(test, "failed", err)

            def addSkip(self, test, reason):
                super().addSkip(test, reason)
                self._done(test, "skipped", reason=reason)

            def addExpectedFailure(self, test, err):
                super().addExpectedFailure(test, err)
                self._done(test, "passed")

            def addUnexpectedSuccess(self, test):
                super().addUnexpectedSuccess(test)
                self._done(test, "failed", reason="unexpected success")


        def run(top, ids, start, pattern):
            loader = unittest.TestLoader()
            sys.path.insert(0, top or os.getcwd())
            suite = loader.loadTestsFromNames(ids) if ids else loader.discover(start, pattern=pattern, top_level_dir=top or None)
            result = unittest.TextTestRunner(resultclass=Result, verbosity=2, stream=sys.stderr).run(suite)
            return 0 if result.wasSuccessful() else 1


        if __name__ == "__main__":
            if sys.argv[1] == "discover":
                discover(sys.argv[2], sys.argv[3], sys.argv[4])
            else:
                sys.exit(run(sys.argv[2], sys.argv[5:], sys.argv[3], sys.argv[4]))
        """;

    /// <summary>A fresh directory with both helpers (delete it with <see cref="Engine.TestProviderContext.DeleteQuietly"/>).</summary>
    public static string Write()
    {
        var dir = Engine.TestProviderContext.NewTempDirectory();
        File.WriteAllText(Path.Combine(dir, PytestPlugin + ".py"), PytestPluginSource.ReplaceLineEndings("\n") + "\n");
        File.WriteAllText(Path.Combine(dir, UnittestScript), UnittestSource.ReplaceLineEndings("\n") + "\n");
        return dir;
    }
}
