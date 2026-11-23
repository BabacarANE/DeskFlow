export const environment = {
  /**
   * Production and default builds: no simulated login URL. The `mock` configuration replaces
   * this file (angular.json fileReplacements) so nothing of the mock reaches production.
   */
  mockExternalLoginUrl: null as ((invitation?: string | null) => string) | null,
};
