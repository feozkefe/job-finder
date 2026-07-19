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

  return card;
}

// ---------- For Me ----------

let fmData = null;
let fmLevel = "all";

function renderMatches() {
  fmResults.innerHTML = "";
  if (!fmData) return;

  const visible = fmData.jobs.filter(m => fmLevel === "all" || m.seniority === fmLevel);
  for (const m of visible) fmResults.appendChild(jobCard(m.job, m));

  const counts = {};
  for (const m of fmData.jobs) counts[m.seniority] = (counts[m.seniority] || 0) + 1;
  const breakdown = ["Junior", "Mid", "Senior", "Any"]
    .filter(l => counts[l])
    .map(l => `${counts[l]} ${l === "Any" ? "unspecified" : l.toLowerCase()}`)
    .join(", ");

  fmStatus.textContent =
    `Showing ${visible.length} of ${fmData.jobs.length} jobs (${breakdown}) out of ${fmData.scanned} scanned — ` +
    `excluded: ${fmData.excludedGermanRequired} German required, ${fmData.excludedGermanContent} German content`;
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
  fmLevel = btn.dataset.level;
  for (const b of document.querySelectorAll("#fm-levels .level"))
    b.classList.toggle("active", b === btn);
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

loadSites().then(() => runMatch());
