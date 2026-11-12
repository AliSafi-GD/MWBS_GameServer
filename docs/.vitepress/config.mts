import { defineConfig } from 'vitepress'

// GitHub Pages serves this repository at https://<user>.github.io/<repo>/
// so every URL needs the repository name as a prefix. Change it if you rename the repository.
const base = process.env.DOCS_BASE ?? '/MWBS_GameServer/'
const repo = 'https://github.com/AliSafi-GD/MWBS_GameServer'

export default defineConfig({
  title: 'GameServer',
  description: 'A small, self-hosted game backend in C# with a Unity client SDK.',
  base,
  cleanUrls: true,
  lastUpdated: true,
  // The docs link to the local development server (http://localhost:5000/...), which is not part of this site.
  ignoreDeadLinks: [/^https?:\/\/localhost/],
  head: [['link', { rel: 'icon', href: `${base}favicon.svg` }]],

  themeConfig: {
    logo: '/favicon.svg',
    siteTitle: 'GameServer',
    nav: [
      { text: 'Getting started', link: '/getting-started/introduction' },
      { text: 'Server', link: '/server/configuration' },
      { text: 'Unity client', link: '/client/installation' },
      { text: 'Guides', link: '/guides/tank-arena' },
      { text: 'Reference', link: '/reference/rest-api' },
    ],
    sidebar: {
      '/getting-started/': [
        {
          text: 'Getting started',
          items: [
            { text: 'Introduction', link: '/getting-started/introduction' },
            { text: 'Requirements', link: '/getting-started/requirements' },
            { text: 'Download the project', link: '/getting-started/download' },
            { text: 'Run the server', link: '/getting-started/run-the-server' },
            { text: 'Run with Docker', link: '/getting-started/docker' },
            { text: 'Your first request', link: '/getting-started/first-request' },
            { text: 'Your first Unity game', link: '/getting-started/first-unity-game' },
          ],
        },
      ],
      '/server/': [
        {
          text: 'Server',
          items: [
            { text: 'Configuration', link: '/server/configuration' },
            { text: 'Authentication', link: '/server/authentication' },
            { text: 'Remote config', link: '/server/remote-config' },
            { text: 'Wallet and inventory', link: '/server/economy' },
            { text: 'Shop', link: '/server/shop' },
            { text: 'Progress and quests', link: '/server/progress' },
            { text: 'Leaderboards and tournaments', link: '/server/leaderboards' },
            { text: 'Notifications', link: '/server/notifications' },
            { text: 'Friends, clans and parties', link: '/server/social' },
            { text: 'Chat and realtime', link: '/server/realtime' },
            { text: 'Matchmaking and matches', link: '/server/matches' },
            { text: 'Admin panel', link: '/server/admin' },
            { text: 'Deploy to production', link: '/server/production' },
          ],
        },
        {
          text: 'Server framework',
          items: [
            { text: 'Writing a module', link: '/server/modules' },
            { text: 'RPC functions', link: '/server/rpc' },
            { text: 'Hooks', link: '/server/hooks' },
            { text: 'Match handlers', link: '/server/match-handlers' },
          ],
        },
      ],
      '/client/': [
        {
          text: 'Unity client',
          items: [
            { text: 'Installation', link: '/client/installation' },
            { text: 'Quick start', link: '/client/quick-start' },
            { text: 'Authentication', link: '/client/authentication' },
            { text: 'Account and config', link: '/client/account-config' },
            { text: 'Wallet, shop and progress', link: '/client/economy-progress' },
            { text: 'Leaderboards', link: '/client/leaderboards' },
            { text: 'Social and chat', link: '/client/social-chat' },
            { text: 'Realtime', link: '/client/realtime' },
            { text: 'Matchmaking and matches', link: '/client/matches' },
            { text: 'Errors', link: '/client/errors' },
            { text: 'Architecture tips', link: '/client/architecture' },
            { text: 'Platforms and transports', link: '/client/platforms' },
          ],
        },
      ],
      '/guides/': [
        {
          text: 'Guides',
          items: [
            { text: 'Tank Arena demo game', link: '/guides/tank-arena' },
            { text: 'Build an Android APK', link: '/guides/android' },
            { text: 'Testing', link: '/guides/testing' },
            { text: 'Troubleshooting', link: '/guides/troubleshooting' },
          ],
        },
      ],
      '/reference/': [
        {
          text: 'Reference',
          items: [
            { text: 'REST API', link: '/reference/rest-api' },
            { text: 'Realtime protocol', link: '/reference/realtime-protocol' },
            { text: 'Config keys', link: '/reference/config-keys' },
            { text: 'Settings and environment', link: '/reference/settings' },
            { text: 'Error codes', link: '/reference/error-codes' },
            { text: 'Admin API', link: '/reference/admin-api' },
          ],
        },
      ],
    },
    socialLinks: [{ icon: 'github', link: repo }],
    editLink: {
      pattern: `${repo}/edit/main/docs/:path`,
      text: 'Edit this page on GitHub',
    },
    search: { provider: 'local' },
    outline: [2, 3],
    footer: { message: 'GameServer documentation', copyright: 'Source code and docs live in one repository.' },
  },
})
