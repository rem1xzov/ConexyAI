export type ConexyModel = 'ConexyV1-flash' | 'ConexyV1-pro' | 'conexy-coder';

export type ReasoningEffort = 'low' | 'high' | 'max';

export interface TaskAttachment {
  fileName: string;
  contentBase64: string;
  contentType: string; // e.g. "image/png", "text/plain", "application/json"
}

export interface ConexyRequest {
  model: ConexyModel;
  prompt: string;
  githubToken?: string;
  githubRepo?: string;
  attachments?: TaskAttachment[];
  thinking?: boolean;
  reasoningEffort?: ReasoningEffort;
  studentsMode?: boolean;
  sessionId?: string;
  chatId?: string;
  smartSearch?: boolean;
  // INCOGNITO_CHAT: добавлено 2026-09-20
  /** Ephemeral turn: not persisted to chat history and excluded from long-term memory. */
  incognito?: boolean;
  // CONTINUE_GENERATION: добавлено 2026-09-21
  /** Already-generated answer text; the model resumes from here instead of starting over. */
  assistantPrefix?: string;
}

export interface ConexyResponse {
  id: string;
  userId: string;
  model: string;
  prompt: string;
  result?: string | null;
  status: string;
  createdAt: string;
  finishedAt?: string | null;
}

// SUBSCRIPTION_TIERS: добавлено 2026-09-17
export interface SubscriptionUsage {
  tier: string;
  flashUsed: number;
  flashLimit: number;
  flashResetsAt: string;
  proUsed: number;
  proLimit: number;
  proResetsAt: string;
  agentUsed: number;
  agentLimit: number;
  agentResetsAt: string;
}

export interface LimitExceededInfo {
  limit: string;
  resetsAt: string;
}

export interface DevTokenResponse {
  token: string;
  expiresAtUtc: string;
  lifetimeMinutes: number;
}

// EMAIL_AUTH: добавлено 2026-09-19
export interface UserProfile {
  email: string;
  displayName: string;
  tier: string;
  isAdmin: boolean;
}

// ADMIN_PANEL: добавлено 2026-09-19
export interface AdminUser {
  id: string;
  email: string | null;
  gitHubUsername: string | null;
  createdAt: string;
  tier: string;
  isAdmin: boolean;
  isSuperAdmin: boolean;
}

export interface AdminUsersResponse {
  totalCount: number;
  page: number;
  pageSize: number;
  users: AdminUser[];
}

// SUPPORT: добавлено 2026-09-19
export interface SupportMessage {
  id: string;
  ticketId: string;
  senderId: string;
  content: string;
  createdAt: string;
  isFromAdmin: boolean;
}

export interface SupportTicket {
  id: string;
  userId: string;
  status: string;
  createdAt: string;
  lastMessageAt: string;
  messages: SupportMessage[];
}

export interface AdminSupportTicket {
  id: string;
  userId: string;
  userEmail: string | null;
  userGitHubUsername: string | null;
  status: string;
  createdAt: string;
  lastMessageAt: string;
  lastMessagePreview: string | null;
}

export interface WorkspaceFileEntry {
  name: string;
  path: string;
  isDirectory: boolean;
  size: number;
  children: WorkspaceFileEntry[];
}

export interface WorkspaceListing {
  files: string[];
  tree: WorkspaceFileEntry[];
}

export interface WorkspaceFileContent {
  path: string;
  name: string;
  content: string;
}

export interface SaveFileDto {
  path: string;
  content?: string;
}

/** Content response from the IDE file API (TZ_08 /api/sessions/{id}/files/content). */
export interface IdeFileContent {
  path: string;
  content: string;
  isBinary: boolean;
}
