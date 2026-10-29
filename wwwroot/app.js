const form = document.getElementById("search-form");
const siteSelect = document.getElementById("site");
const statusEl = document.getElementById("status");
const resultsEl = document.getElementById("results");
const loadMoreBtn = document.getElementById("load-more");
const searchBtn = document.getElementById("search-btn");

const fmSite = document.getElementById("fm-site");
const fmRun = document.getElementById("fm-run");
const fmStatus = document.getElementById("fm-status");
const fmResults = document.getElementById("fm-results");
const fmIncludeGerman = document.getElementById("fm-include-german");

let currentPage = 1;
let currentParams = null;
let shownCount = 0;

// ---------- per-job status (applied / saved / rejected), persisted server-side ----------

let statuses = {};

async function loadStatuses() {
  try { statuses = await fetch("/api/statuses").then(r => r.json()); }
  catch { statuses = {}; }
}

function jobKey(job) {
  return `${job.source}:${job.siteJobId}`;
}

async function setJobStatus(job, status) {
  const key = jobKey(job);
  const next = statuses[key]?.status === status ? null : status; // click again to clear
  if (next) statuses[key] = { status: next, title: job.title, company: job.company };
  else delete statuses[key];
  try {
    await fetch("/api/statuses", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ key, status: next, title: job.title, company: job.company }),
    });
  } catch { /* keep the optimistic local state */ }
}

function applyStatusVisual(card, job) {
  const s = statuses[jobKey(job)]?.status;
  card.classList.toggle("st-applied", s === "applied");
  card.classList.toggle("st-saved", s === "saved");
  card.classList.toggle("st-rejected", s === "rejected");
  for (const b of card.querySelectorAll(".status-btn"))
    b.classList.toggle("on", b.dataset.status === s);
}

// ---------- AI workflow (evaluate / tailor CV / cover letter), stored server-side ----------

let evaluations = {};

async function loadEvaluations() {
  try { evaluations = await fetch("/api/ai/evaluations").then(r => r.json()); }
  catch { evaluations = {}; }
}

// must match ApplicationService.SafeKey
function aiKey(job) {
  return jobKey(job).toLowerCase().replace(/[^a-z0-9_-]+/g, "-").replace(/^-+|-+$/g, "");
}

async function postAi(path, job, extra = {}) {
  const send = description => fetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ job, description, ...extra }),
  }).then(async r => ({ ok: r.ok, data: await r.json().catch(() => ({ error: `Request failed (${r.status})` })) }));

  let res = await send(null);
  if (!res.ok && /description/i.test(res.data.error || "")) {
    const pasted = prompt("Couldn't read this job's description. Paste it here:");
    if (pasted) res = await send(pasted);
  }
  if (!res.ok) throw new Error(res.data.error || "Request failed");
  return res.data;
}

function renderEvaluation(box, ev) {
  box.innerHTML = "";
  box.hidden = false;
  const head = document.createElement("div");
  head.className = "ai-head";
  head.append(
    chip(`${ev.grade} · ${ev.fitScore}`, `grade grade-${ev.grade.toLowerCase()}`),
    chip(ev.verdict, `verdict verdict-${ev.verdict}`));
  if (ev.germanRequired) head.appendChild(chip("German required", "warn"));
  if (ev.coverLetterRecommended) head.appendChild(chip("Cover letter recommended", "reason"));
  box.appendChild(head);

  const p = document.createElement("p");
  p.textContent = `${ev.roleSummary} ${ev.seniorityFit}`;
  box.appendChild(p);

  for (const [title, items, cls] of [["Matches", ev.matchedRequirements, "ok"], ["Gaps", ev.gaps, "gap"]]) {
    if (!items?.length) continue;
    const h = document.createElement("h4");
    h.textContent = title;
    const ul = document.createElement("ul");
    ul.className = cls;
    for (const i of items) ul.appendChild(Object.assign(document.createElement("li"), { textContent: i }));
    box.append(h, ul);
  }

  if (ev.atsKeywords?.length) {
    const h = document.createElement("h4");
    h.textContent = "ATS keywords";
    const chips = document.createElement("div");
    chips.className = "chips";
    for (const k of ev.atsKeywords) chips.appendChild(chip(k.keyword, k.inCv ? "matched" : "missing"));
    box.append(h, chips);
  }
  if (ev.notes) {
    const n = document.createElement("p");
    n.className = "ai-notes";
    n.textContent = ev.notes;
    box.appendChild(n);
  }
}

function aiPanel(job, h3) {
  const wrap = document.createElement("div");
  wrap.className = "ai";
  const buttons = document.createElement("div");
  buttons.className = "ai-buttons";
  const box = document.createElement("div");
  box.className = "ai-box";
  box.hidden = true;
  const status = document.createElement("span");
  status.className = "ai-status";

  const showGrade = ev => {
    h3.querySelector(".grade")?.remove();
    h3.appendChild(chip(ev.grade, `grade grade-${ev.grade.toLowerCase()}`));
  };
  const existing = evaluations[aiKey(job)];
  if (existing) showGrade(existing);

  const run = async (btn, label, action) => {
    for (const b of buttons.querySelectorAll("button")) b.disabled = true;
    status.textContent = `${label}… (Claude, ~10–60 s)`;
    status.classList.remove("error");
    try {
      await action();
      status.textContent = "";
    } catch (err) {
      status.textContent = err.message;
      status.classList.add("error");
    } finally {
      for (const b of buttons.querySelectorAll("button")) b.disabled = false;
    }
  };

  const evalBtn = document.createElement("button");
  evalBtn.textContent = existing ? `Fit: ${existing.grade}` : "Evaluate fit";
  evalBtn.title = "Claude grades this job against your CV (shift-click to re-run)";
  evalBtn.addEventListener("click", e => {
    const cached = evaluations[aiKey(job)];
    if (cached && !e.shiftKey) {
      if (box.hidden) renderEvaluation(box, cached); else box.hidden = true;
      return;
    }
    run(evalBtn, "Evaluating", async () => {
      const ev = await postAi("/api/ai/evaluate", job, { force: e.shiftKey });
      evaluations[aiKey(job)] = ev;
      evalBtn.textContent = `Fit: ${ev.grade}`;
      showGrade(ev);
      renderEvaluation(box, ev);
    });
  });

  const cvBtn = document.createElement("button");
  cvBtn.textContent = "Tailor CV";
  cvBtn.title = "Generate an ATS-friendly CV for this job (opens a printable page)";
  cvBtn.addEventListener("click", () => {
    const tab = window.open("about:blank", "_blank");
    run(cvBtn, "Tailoring CV", async () => {
      const { url } = await postAi("/api/ai/cv", job);
      if (tab) tab.location = url; else window.open(url, "_blank");
    }).then(() => { if (tab && tab.location.href === "about:blank") tab.close(); });
  });

  const clBtn = document.createElement("button");
  clBtn.textContent = "Cover letter";
  clBtn.addEventListener("click", () => {
    const tab = window.open("about:blank", "_blank");
    run(clBtn, "Writing cover letter", async () => {
      const { url } = await postAi("/api/ai/cover-letter", job);
      if (tab) tab.location = url; else window.open(url, "_blank");
    }).then(() => { if (tab && tab.location.href === "about:blank") tab.close(); });
  });

  buttons.append(evalBtn, cvBtn, clBtn, status);
  wrap.append(buttons, box);
  return wrap;
}

document.getElementById("base-cv").addEventListener("click", async e => {
  const btn = e.currentTarget;
  const tab = window.open("about:blank", "_blank");
  btn.disabled = true;
  btn.textContent = "Generating…";
  try {
    const res = await fetch("/api/ai/base-cv", { method: "POST" });
    const data = await res.json();
    if (!res.ok) throw new Error(data.error || `Request failed (${res.status})`);
    if (tab) tab.location = data.url;
  } catch (err) {
    tab?.close();
    alert(err.message);
  } finally {
    btn.disabled = false;
    btn.textContent = "Base ATS CV";
  }
});

// ---------- tabs ----------

const tabs = {
  forme: [document.getElementById("tab-forme"), document.getElementById("panel-forme")],
  search: [document.getElementById("tab-search"), document.getElementById("panel-search")],
};

function showTab(name) {
  for (const [key, [btn, panel]] of Object.entries(tabs)) {
    btn.classList.toggle("active", key === name);
    panel.hidden = key !== name;
  }
}

tabs.forme[0].addEventListener("click", () => showTab("forme"));
tabs.search[0].addEventListener("click", () => showTab("search"));

// ---------- shared helpers ----------

async function loadSites() {
  const sites = await fetch("/api/sites").then(r => r.json());
  for (const sel of [siteSelect, fmSite]) {
    sel.innerHTML = "";
    if (sel === fmSite) {
      const all = document.createElement("option");
      all.value = "all";
      all.textContent = "All sites";
      sel.appendChild(all);
    }
    for (const site of sites) {
      const opt = document.createElement("option");
      opt.value = site.id;
      opt.textContent = site.name;
      sel.appendChild(opt);
    }
  }
}

function chip(text, cls = "") {
  const el = document.createElement("span");
  el.className = `chip ${cls}`.trim();
  el.textContent = text;
  return el;
}

function jobCard(job, extras = null) {
  const card = document.createElement("article");
  card.className = "job-card";

  const logo = job.logoUrl
    ? Object.assign(document.createElement("img"), { className: "logo", src: job.logoUrl, alt: "" })
    : Object.assign(document.createElement("div"), { className: "logo placeholder", textContent: (job.company || "?")[0].toUpperCase() });
  card.appendChild(logo);

  const body = document.createElement("div");
  body.className = "body";

  const h3 = document.createElement("h3");
  const link = document.createElement("a");
  link.href = job.url;
  link.target = "_blank";
  link.rel = "noopener";
  link.textContent = job.title;
  h3.appendChild(link);
  if (extras?.score !== undefined) {
    const badge = document.createElement("span");
    badge.className = "score";
    badge.textContent = extras.score;
    badge.title = "Match score";
    h3.appendChild(badge);
  }
  body.appendChild(h3);

  const meta = document.createElement("div");
  meta.className = "meta";
  const parts = [job.company, job.location];
  if (job.publishedAt) parts.push(new Date(job.publishedAt).toLocaleDateString());
  if (job.salary) parts.push(job.salary);
  if (extras?.contract) parts.push(extras.contract);
  meta.append(...parts.filter(Boolean).flatMap((p, i) => {
    const span = document.createElement("span");
    span.textContent = p;
    if (i === 0) return [span];
    const sep = document.createElement("span");
    sep.className = "sep";
    sep.textContent = "·";
    return [sep, span];
  }));
  body.appendChild(meta);

  const chips = document.createElement("div");
  chips.className = "chips";
  if (extras) {
    if (extras.seniority && extras.seniority !== "Any")
      chips.appendChild(chip(extras.seniority, `level-${extras.seniority.toLowerCase()}`));
    for (const r of extras.reasons || []) chips.appendChild(chip(r, "reason"));
    if (extras.requiresGerman) chips.appendChild(chip("German required", "warn"));
    if (!extras.detailsFetched) chips.appendChild(chip("Details unavailable", "warn"));
    for (const s of extras.matchedSkills || []) chips.appendChild(chip(s, "matched"));
    const rest = (job.skills || []).filter(s => !(extras.matchedSkills || []).includes(s));
    for (const s of rest.slice(0, 5)) chips.appendChild(chip(s));
  } else {
    if (job.language) chips.appendChild(chip(job.language, "lang"));
    if (job.remote) chips.appendChild(chip("Remote", "remote"));
    for (const s of job.seniorities || []) chips.appendChild(chip(s, "seniority"));
    for (const s of (job.skills || []).slice(0, 8)) chips.appendChild(chip(s));
  }
  if (job.remote && extras) chips.appendChild(chip("Remote", "remote"));
  body.appendChild(chips);
  body.appendChild(aiPanel(job, h3));

  card.appendChild(body);

  if (extras?.applyUrl) {
    const apply = document.createElement("a");
    apply.className = "apply";
    apply.href = extras.applyUrl;
    apply.target = "_blank";
    apply.rel = "noopener";
    apply.textContent = "Apply ↗";
    card.appendChild(apply);
  }

  const actions = document.createElement("div");
  actions.className = "card-actions";
  for (const [status, icon, label] of [
    ["saved", "★", "Save"],
    ["applied", "✓", "Applied"],
    ["rejected", "✕", "Not suitable"],
  ]) {
    const btn = document.createElement("button");
    btn.className = `status-btn ${status}`;
    btn.dataset.status = status;
    btn.textContent = icon;
    btn.title = label;
    btn.addEventListener("click", async () => {
      await setJobStatus(job, status);
      applyStatusVisual(card, job);
      if (!tabs.forme[1].hidden) renderMatches();
    });
    actions.appendChild(btn);
  }
  card.appendChild(actions);
  applyStatusVisual(card, job);

  return card;
}

// ---------- For Me ----------

let fmData = null;
let fmLevel = "all";
let fmSort = "date";
let fmStatusFilter = "all";

function compareMatches(a, b) {
  const dateOf = m => (m.job.publishedAt ? Date.parse(m.job.publishedAt) : 0);
  if (fmSort === "date") {
    // newest first; undated jobs sink to the bottom, ties broken by score
    return (dateOf(b) - dateOf(a)) || (b.score - a.score);
  }
  return (b.score - a.score) || (dateOf(b) - dateOf(a));
}

function renderMatches() {
  fmResults.innerHTML = "";
  if (!fmData) return;

  const statusOf = m => statuses[jobKey(m.job)]?.status;
  const visible = fmData.jobs
    .filter(m => fmLevel === "all" || m.seniority === fmLevel)
    .filter(m => fmStatusFilter === "all"
      ? statusOf(m) !== "rejected"           // "not suitable" jobs stay out of the way
      : statusOf(m) === fmStatusFilter)
    .sort(compareMatches);
  for (const m of visible) fmResults.appendChild(jobCard(m.job, m));

  const counts = {};
  const statusCounts = { saved: 0, applied: 0, rejected: 0 };
  for (const m of fmData.jobs) {
    counts[m.seniority] = (counts[m.seniority] || 0) + 1;
    const s = statusOf(m);
    if (s) statusCounts[s]++;
  }
  const breakdown = ["Junior", "Mid", "Senior", "Any"]
    .filter(l => counts[l])
    .map(l => `${counts[l]} ${l === "Any" ? "unspecified" : l.toLowerCase()}`)
    .join(", ");

  fmStatus.textContent =
    `Showing ${visible.length} of ${fmData.jobs.length} jobs (${breakdown}) out of ${fmData.scanned} scanned — ` +
    `excluded: ${fmData.excludedGermanRequired} German required, ${fmData.excludedGermanContent} German content — ` +
    `★ ${statusCounts.saved} saved, ✓ ${statusCounts.applied} applied, ✕ ${statusCounts.rejected} not suitable`;
}

async function runMatch() {
  fmRun.disabled = true;
  fmResults.innerHTML = "";
  fmStatus.textContent = "Scanning jobs and reading details… first run can take a minute, later runs are cached.";
  fmStatus.classList.remove("error");

  try {
    const params = new URLSearchParams({
      site: fmSite.value,
      includeGermanRequired: fmIncludeGerman.checked,
    });
    const res = await fetch(`/api/match?${params}`);
    const data = await res.json();
    if (!res.ok) throw new Error(data.error || `Request failed (${res.status})`);
    fmData = data;
    renderMatches();
  } catch (err) {
    fmStatus.textContent = err.message;
    fmStatus.classList.add("error");
  } finally {
    fmRun.disabled = false;
  }
}

fmRun.addEventListener("click", runMatch);

document.getElementById("fm-levels").addEventListener("click", e => {
  const btn = e.target.closest(".level");
  if (!btn) return;
  if (btn.dataset.sort) {
    fmSort = btn.dataset.sort;
    for (const b of document.querySelectorAll("#fm-levels .sort"))
      b.classList.toggle("active", b === btn);
  } else if (btn.dataset.status) {
    fmStatusFilter = btn.dataset.status;
    for (const b of document.querySelectorAll("#fm-levels .st"))
      b.classList.toggle("active", b === btn);
  } else {
    fmLevel = btn.dataset.level;
    for (const b of document.querySelectorAll("#fm-levels .level:not(.sort):not(.st)"))
      b.classList.toggle("active", b === btn);
  }
  renderMatches();
});

// ---------- manual search ----------

function readFilters() {
  return {
    site: siteSelect.value,
    skill: document.getElementById("skill").value.trim(),
    city: document.getElementById("city").value.trim(),
    country: document.getElementById("country").value.trim(),
    englishOnly: document.getElementById("english-only").checked,
  };
}

async function fetchJobs(page) {
  const params = new URLSearchParams({ ...currentParams, page });
  const res = await fetch(`/api/jobs?${params}`);
  const data = await res.json();
  if (!res.ok) throw new Error(data.error || `Request failed (${res.status})`);
  return data;
}

function setStatus(text, isError = false) {
  statusEl.textContent = text;
  statusEl.classList.toggle("error", isError);
}

async function search(reset) {
  if (reset) {
    currentParams = readFilters();
    currentPage = 1;
    shownCount = 0;
    resultsEl.innerHTML = "";
  } else {
    currentPage++;
  }

  searchBtn.disabled = true;
  loadMoreBtn.disabled = true;
  setStatus(reset ? "Searching…" : "Loading more…");

  try {
    const data = await fetchJobs(currentPage);
    for (const job of data.jobs) resultsEl.appendChild(jobCard(job));
    shownCount += data.jobs.length;

    const total = data.totalJobs ? ` (site reports ~${data.totalJobs} total, all languages)` : "";
    setStatus(shownCount === 0 ? "No jobs found." : `Showing ${shownCount} jobs${total}`);
    loadMoreBtn.hidden = !data.hasMore;
  } catch (err) {
    setStatus(err.message, true);
    loadMoreBtn.hidden = true;
  } finally {
    searchBtn.disabled = false;
    loadMoreBtn.disabled = false;
  }
}

form.addEventListener("submit", e => {
  e.preventDefault();
  search(true);
});

loadMoreBtn.addEventListener("click", () => search(false));

Promise.all([loadStatuses(), loadEvaluations()]).then(loadSites).then(() => runMatch());
