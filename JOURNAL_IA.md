# Journal de bord IA — Prototype Foule Urbaine

**Auteur** : Julien Atome
**Projet** : Prototype Foule Urbaine (Forma Studio — Scénario A : Unity DOTS / ECS)
**Période** : 18 mai 2026 → 21 mai 2026
**Outil IA principal** : **Claude (Anthropic)** — modèles Opus 4.5 puis Opus 4.7 via Claude Code (CLI agentique avec accès direct au filesystem du projet)
**Outils secondaires testés** : GitHub Copilot (auto-complétion ponctuelle dans Rider)

Le présent journal liste chronologiquement chaque session d'interaction IA significative ayant abouti à du code intégré au prototype. Pour chaque session : date, outil, but, prompt synthétisé, résultat, nombre d'itérations, verdict.

> **Convention** : la colonne *itérations* compte le nombre de cycles "prompt → réponse → test → ajustement" avant que le code soit accepté tel quel. Une itération = un échange complet, pas un message.

---

## Méthodologie suivie

1. **Cadrage** : chaque grande feature commence par un prompt de "design", pas de code, pour aligner l'IA sur l'architecture existante (DOTS conventions, namespaces, patterns).
2. **Génération** : l'IA produit code + commentaires inline. Aucune saisie manuelle, copier-coller direct des outputs.
3. **Validation Unity** : Play en éditeur, observation visuelle + Entities Hierarchy, lecture Console.
4. **Itération** : sur erreur / comportement incorrect, fournir à l'IA le contexte (logs, captures texte du Profiler, description du bug) et demander une correction ciblée — jamais éditer à la main.
5. **Documentation côté IA** : à chaque grande étape, demander à l'IA de mettre à jour `RAPPORT_DEVELOPPEMENT_IA.md` et `ROADMAP_SIMULATION.md` pour que la session suivante reprenne avec contexte propre.

---

## Tableau chronologique des sessions

| # | Date | Outil | Sujet | Prompt (synthèse) | Itérations | Résultat | Verdict |
|---|------|-------|-------|-------------------|------------|----------|---------|
| 1 | 2026-05-18 | Claude Opus | **Setup ECS from scratch** | "Crée un projet Unity ECS qui spawn N agents, les fait suivre des waypoints, avec séparation, 3 comportements (Hurried/Walker/Stationary), HUD FPS+count. Architecture DOTS propre." | 2 | Architecture complète livrée : `AgentComponents`, `AgentAuthoring`, `PathAuthoring`, `CrowdSpawnerAuthoring`, `CrowdSpawnerSystem`, `AgentSteeringSystem` (spatial hash + séparation), `AgentMovementSystem`, `CrowdHUD`. Compile au 1er essai. | ✅ Excellent — l'IA a structuré les conventions ECS correctement (`IComponentData` unmanaged, `[BurstCompile]`, EntityCommandBuffer, spatial hash en `NativeParallelMultiHashMap`). |
| 2 | 2026-05-18 | Claude Opus | **VAT animation pipeline (capsules → mannequins animés)** | "Remplace le rendu capsule par un système d'animation skinné performant. Cible 5k+ agents sans dégrader le FPS." | 4 | Pipeline VAT complet : `VATBakerWindow` (éditeur), `VATAsset` (ScriptableObject), shader HLSL `AgentVAT.shader` avec DOTS Instancing, `AgentAnimationSystem` (Idle/Walk selon vitesse), `AgentLODBakingSystem`, `PropagateMaterialPropsToLODSystem` (mirroir runtime root → LOD children). | ✅ Très bon sur la théorie VAT et l'archi des property components. ⚠️ A pris 2 itérations pour bien comprendre le pipeline LOD + Entities Graphics (Entities Graphics baked chaque enfant LOD en entité séparée, il fallait propager les MaterialProperty). |
| 3 | 2026-05-18 | Claude Opus | **Décimation mesh pour LOD** | "Réduis le mesh source en N niveaux de LOD via clustering de vertices, sans casser le baking VAT." | 3 | `ClusterDecimator.cs` (éditeur) qui groupe les vertices par cellules + génère mesh allégé. | ✅ Algo correct. ⚠️ Premier essai produisait des trous dans le mesh (clustering trop agressif), corrigé par une seconde itération avec sampling pondéré. |
| 4 | 2026-05-20 | Claude Opus | **Phase 1 — Obstacles statiques** | "Ajoute des obstacles bakés (Box/Circle, AABB orientée) que les agents évitent par répulsion + pushout dans le movement system. Spatial hash pour rester < 5% perte FPS." | 3 | `ObstacleComponents` + `ObstacleMath`, `ObstacleAuthoring` avec gizmos, `ObstacleSpatialIndexSystem` one-shot, force de répulsion + wall-sliding dans `AgentSteeringSystem`. Stuck-skip ajouté à `AgentMovement` (timer 2s → saut de waypoint). | ✅ Excellent. ⚠️ Première version : agents bloqués face au mur quand le waypoint cible était de l'autre côté → IA a proposé spontanément wall-sliding + stuck-detection à la 2ème itération. |
| 5 | 2026-05-20 | Claude Opus | **Phase 2 — Zones marchables (navmesh-light)** | "Définit des zones explicites où les agents ont le droit de marcher. Hors zone → snap au bord. Compatible avec Phase 1." | 1 | `WalkableComponents`, `WalkableAreaAuthoring` (gizmo vert slab), `WalkableSpatialIndexSystem`, snap-to-boundary dans `AgentMovementSystem`. | ✅ One-shot réussi. L'IA a réutilisé `ObstacleMath` (refactor partagé), bonne factorisation. |
| 6 | 2026-05-20 | Claude Opus | **Phase 3 — ORCA-lite + behavior dynamics** | "Anticipation des trajectoires voisines (TTC) au lieu de séparation pure. Et swap dynamique Walker↔Hurried sur Idle→Traveling." | 2 | `AgentSpatialData` étendu avec Velocity, calcul TTC inline dans le neighbor sweep, tie-break head-on par parité d'entity index. `BaseBehavior` + `BaseSpeed` ajoutés à `AgentTypeData`. | ✅ Implémentation théorique sans bug majeur. ⚠️ Edge case `Stationary timer = 0` non géré au premier essai → fix par garde explicite. |
| 7 | 2026-05-20 | Claude Opus | **Phase 4 — Points d'intérêt** | "Les agents choisissent un POI comme destination, s'y arrêtent, attendent un dwell time, repartent. Capacity par POI." | 2 | `POIComponents`, `POIAuthoring` (gizmo color-coded), `AgentGoalSystem` single-thread (mutations partagées sur `CurrentOccupancy`), override du desired dans le steering selon goal state. | ✅ Bon. ⚠️ Bug "early-exit foireux quand buffer POI vide à runtime" identifié et corrigé à la 2ème itération. |
| 8 | 2026-05-21 | Claude Opus | **Phase 11 — Infrastructure routière** | "Ajoute des zones Road (interdites aux piétons) et Crosswalk (exemption). Les piétons doivent rester sur les trottoirs, sauf via crosswalk." | 1 | `RoadComponents` + `CrosswalkComponents`, authorings avec gizmos différenciés (gris hachuré / zebra blanc), spatial index systems miroirs, pushout route + exception crosswalk dans `AgentMovementSystem`. | ✅ One-shot. L'IA a réutilisé le pattern existant des spatial index systems (DRY évident). |
| 9 | 2026-05-21 | Claude Opus | **Phase 12 — Voitures statiques** | "Voitures sur des lanes orientées (≠ paths bouclés piétons). Accel/brake asymétrique, slerp rotation, intersections aléatoires. Pas d'animation." | 2 | `CarComponents` (archetype séparé d'`AgentTag`), `LaneAuthoring` orienté avec flèches gizmos, `CarSpawnerSystem` round-robin, `CarLaneFollowingSystem` parallèle Burst, `CarMovementSystem`. | ✅ Très bon. ⚠️ Au 1er test : aucune voiture spawn. Diagnostic IA → checklist (Console logs, lanes ≥ 2 waypoints, Subscene, prefab). User a identifié le manque de waypoints → fix immédiat sans modifier le code. |
| 10 | 2026-05-21 | Claude Opus | **Glitch rotation : agents tournent en boucle aux bords de route** | "Les agents qui veulent traverser une route se mettent à osciller en rotation et avancent à peine. Trouve et corrige." | 1 | Diagnostic correct : steering ne connaît pas les routes → desired pointe dedans → MovementSystem snap → velocity flippe entre 2 directions → rotation suit la velocity et flippe. Fix : wall-sliding route dans le steering + slerp de rotation dans le movement. | ✅ Diagnostic clair, fix appliqué proprement en une passe. |
| 11 | 2026-05-21 | Claude Opus | **Bug résiduel : agents qui traversent la route entière + drift hors trottoir** | "Certains agents disparaissent en marchant en travers, d'autres dévient hors du trottoir à des spots précis." | 1 | Diagnostic en 3 points : (a) sign bug `away = pos - closest` pointe vers le centre de la route au lieu du bord le plus proche → corrigé via `closest - pos`. (b) pas de force active de répulsion route → ajoutée. (c) pas de wall-sliding sur zones marchables → ajouté avec probe overlap-aware. | ✅ Excellent. L'IA a expliqué chaque bug en référence à la géométrie, pas juste "ça marche maintenant". |
| 12 | 2026-05-21 | Claude Opus | **Runtime control du count pour démo live** | "J'ai besoin de pouvoir augmenter/diminuer le nombre d'agents en temps réel pendant la démo. HUD + raccourcis clavier." | 1 | `CrowdRuntimeTarget` (singleton mutable), `CrowdRuntimeControlSystem` qui réconcilie le count (spawn en batches, despawn ECB), HUD étendu avec boutons +/- et hotkeys 1/2/3/4/5 + presets. | ✅ One-shot. Spawn cappé à 500/frame pour éviter le stutter quand on saute de 500 à 10k. |

---

## Synthèse — Ce que l'IA a bien géré

### 1. Architecture DOTS conforme aux conventions
À aucun moment l'IA n'a proposé du code "managed" (classes, références, GC) là où il fallait du blittable. Tous les composants livrés sont des `struct : IComponentData` ou `IBufferElementData` avec types unmanaged, tous les jobs sont `[BurstCompile]`, le spatial hashing utilise `NativeParallelMultiHashMap`. Sans cette discipline, on aurait perdu 10x en perfs au premier scaling test.

**Exemple de prompt qui a bien marché** :
> "Crée un système qui collecte les obstacles au boot et les pack dans un spatial hash. Le système doit s'auto-désactiver après le build initial. Tous les containers Native dans un singleton `ObstacleSpatialIndex` que les autres systèmes peuvent `RequireForUpdate`."

→ Output direct utilisable, avec `OnDestroy` qui dispose proprement les natives. Aucune fuite mémoire.

### 2. Diagnostic basé sur la géométrie / les maths
Pour les bugs de wall-sliding (sessions #10 et #11), l'IA a traçé concrètement les vecteurs (`closest`, `away`, `normal`) avec des positions numériques pour expliquer le sign bug. Pas de "essayons en inversant le signe" — un raisonnement structuré qui a permis de comprendre **pourquoi** le pushout obstacle existant marchait "par accident" (poussée vers la mauvaise sortie mais l'agent finit par sortir quand même).

### 3. Refactor opportuniste
Lorsque deux features partageaient une logique (ex: Obstacle/Walkable/Road utilisent toutes la même math AABB orientée), l'IA a refactoré `ObstacleMath` pour exposer des overloads primitifs partagés au lieu de dupliquer le code dans chaque fichier. Initiative spontanée, sans qu'on le demande.

### 4. Documentation auto-générée
À chaque grande session, l'IA a tenu à jour `RAPPORT_DEVELOPPEMENT_IA.md` et `ROADMAP_SIMULATION.md`. Ces docs ont permis aux sessions suivantes de reprendre sans context-loss complet (sessions séparées dans le temps).

---

## Synthèse — Ce que l'IA n'a pas su faire / a mal fait

### 1. Bug latent dans le pushout obstacle (Phase 1)
La logique de "pushout quand l'agent est dans un obstacle" calcule un vecteur normal du mauvais côté. Le code fonctionne par accident (un cube symétrique : l'opposé est le côté qui mène quand même à une sortie), mais sur une route longue, ça envoie l'agent traverser tout le slab. L'IA ne l'a vu qu'à l'étape 11 quand un cas similaire a été reproduit dans le code route. **Leçon** : l'IA valide la fonctionnalité, pas la justesse géométrique. Un test unitaire (1 case "agent à 0.1m d'un bord") aurait probablement levé le bug plus tôt.

### 2. Edge cases timing / mémoire
- Phase 4 : un buffer POI vide à runtime → early-exit qui empêchait les agents Interacting de décrémenter leur timer → blocage indéfini. Bug latent corrigé seulement après observation visuelle.
- Phase 3 : `Stationary timer = 0` rendait `stallThresholdSq = 0`, donc un test impossible mais pas crashant. L'IA n'a pas spontanément ajouté la garde, il a fallu lui demander.

### 3. Performances réelles vs estimées
L'IA estime souvent les coûts ("~75k closest-point checks/frame pour 5000 agents"). Ces estimations sont OK comme ordre de grandeur mais à valider avec le Profiler — l'IA ne mesure pas, elle suppose.

### 4. Compatibility avec le Unity Editor
Quelques erreurs de compilation après génération :
- 1× missing `using` (corrigé en 1 prompt)
- 1× warning unused variable (`var random` jamais utilisé après removal de feature)
- 0× erreur de runtime cassante depuis le début du projet

### 5. Setup côté éditeur
L'IA ne peut pas drag-and-drop des prefabs ou créer des sous-scènes. À chaque nouvelle Authoring, il faut lui demander explicitement les **instructions de mise en place côté éditeur** (créer GameObject, attacher composant, drag dans Subscene, référencer prefab dans champ). Cas concret : voitures qui ne spawnaient pas → l'IA a immédiatement diagnostiqué (sans modifier le code) que les lanes manquaient de child waypoints.

---

## Estimation gain de temps réel

**Référence** : une semaine de coding classique solo pour un dev senior connaissant déjà DOTS produirait probablement :
- Spawner + steering basique : 1-2 jours
- Animation skinnée pour 5k agents (sans VAT) : 1-2 jours (Animator classique limité à ~1k)
- Tooling éditeur (authorings, gizmos) : 0.5-1 jour
- Obstacles + walkable + paths : 1 jour
- Tuning + bugs : 1 jour

**Total estimé** : 5-7 jours pour un résultat équivalent **mais sans la couche VAT** (qui demande une expertise shader rare) ni les Phases 11+12.

**Réel avec IA** : 4 jours calendaires (18, 20, 21 + 1 jour de tuning), incluant :
- VAT pipeline complet avec LODs
- Phases 0-4 (incluant Phase 3 ORCA-lite, Phase 4 POI)
- Phases 11-12 (routes + voitures)
- Diagnostic + fix de 2 bugs subtils (rotation glitch, sign bug)

**Gain estimé** : **~50-60% en calendaire**, mais cette estimation pondère mal le bénéfice réel :
- L'expertise VAT + shader HLSL que je n'aurais pas seul → +∞ (j'aurais simplement abandonné cette voie)
- Le respect des conventions DOTS (pas d'erreur de débutant qui coûte 1h à chaque fois)
- La documentation auto-générée qui sert directement de support pour ce rapport

**Limite** : sans l'IA, je serais probablement parti sur un projet plus modeste (GameObjects + Animator + 500 agents max), ce qui aurait été plus rapide à livrer mais hors-sujet pour le brief.

---

## Conclusion — Verdict sur le workflow IA-only

**Verdict global : ✅ Faisable, mais lourd en projet réel.**

### Points positifs
- Réduit drastiquement le coût d'entrée sur une stack peu familière (DOTS).
- La documentation auto-générée par l'IA est presque toujours meilleure que ce que j'écrirais à la fin du projet (l'IA documente *en temps réel*, je documenterais *jamais*).
- Excellent pour le pattern-matching architectural (l'IA repère "ce nouveau truc ressemble à ce que t'as déjà fait" et applique).

### Points négatifs
- L'IA ne *mesure* pas, ne *teste* pas. Tous les chiffres de perf doivent venir d'un Profiler humain.
- Edge cases ⇒ debug 2-3 itérations après livraison initiale. La 1ère version "marche" mais cache des bugs.
- Workflow obligé d'éviter les modifs main : chaque tweak de 2 lignes devient un round-trip IA. Coût en temps non négligeable sur la fin de projet.

### Recommandation pour Forma Studio
- **Phase prototype / POC** : workflow IA-only excellent. Permet à un dev seul de couvrir un scope qui demanderait 2-3 personnes.
- **Phase production** : workflow hybride. L'IA reste pour les boilerplates et les explications, mais les hot-paths critiques doivent être écrits/audités humainement. Un bug dans un job Burst tournant à 60 FPS sur 10k agents = pas le moment de découvrir un sign error.
